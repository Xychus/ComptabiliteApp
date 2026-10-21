using Ledger.Projections.Application.Balances;
using Ledger.Projections.Application.Entries;
using Ledger.Projections.Application.Events;
using Ledger.Projections.Application.Processing;
using Ledger.Projections.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using static Ledger.Projections.Tests.Support.JournalBuilder;

namespace Ledger.Projections.Infrastructure.Tests.Contract;

/// <summary>
/// Suite de contrat des ports des projections (Liskov) : session transactionnelle, idempotence,
/// checkpoint, positions, magasins et reconstruction. Toute implémentation doit la passer.
/// </summary>
public abstract class ProjectionStoreContract : IAsyncLifetime
{
    private static readonly DateOnly _march2 = new(2026, 3, 2);
    private static readonly DateOnly _march20 = new(2026, 3, 20);
    private static readonly DateOnly _april3 = new(2026, 4, 3);

    private ProjectionBackend _backend = null!;
    private JournalBuilder _journal = null!;

    public static TheoryData<string> Projections => [.. ProjectionNames.All];

    protected ProjectionBackend Backend => _backend;

    protected JournalBuilder Journal => _journal;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _backend = CreateBackend();
        _journal = new JournalBuilder(_backend.Entity);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _backend.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    [Theory]
    [MemberData(nameof(Projections))]
    public async Task Le_rejeu_reproduit_exactement_la_projection_incrementale(string projection)
    {
        await Backend.DeliverAsync(Scenario.Build(Backend.Entity, Backend.OtherEntity));
        var incremental = await DumpBothAsync(projection);
        var position = await Backend.PositionAsync(projection);

        await Backend.RebuildAsync(projection);

        Assert.Equal(incremental, await DumpBothAsync(projection));
        Assert.Equal(position, await Backend.PositionAsync(projection));
        Assert.Null(await Backend.PositionAsync(projection + ProjectionTables.RebuildSuffix));
    }

    [Theory]
    [MemberData(nameof(Projections))]
    public async Task Deux_rejeux_successifs_donnent_le_meme_resultat(string projection)
    {
        await Backend.DeliverAsync(Scenario.Build(Backend.Entity, Backend.OtherEntity));
        var incremental = await DumpBothAsync(projection);

        await Backend.RebuildAsync(projection);
        await Backend.RebuildAsync(projection);

        Assert.Equal(incremental, await DumpBothAsync(projection));
    }

    [Fact]
    public async Task Un_evenement_relivre_n_a_aucun_effet()
    {
        var stored = await Backend.DeliverAsync(Scenario.Build(Backend.Entity, Backend.OtherEntity));
        var once = await DumpAllAsync();

        await Backend.RedeliverAsync([.. stored]);

        Assert.Equal(once, await DumpAllAsync());
    }

    [Fact]
    public async Task Les_soldes_cumulent_les_ecritures_et_leurs_contre_passations()
    {
        var entryId = Guid.CreateVersion7();
        await Backend.DeliverAsync(
            Journal.Posted(entryId, _march2, Debit("1000", 40m), Credit("4000", 40m)),
            Journal.Posted(_march20, Debit("1000", 2.5m), Credit("4000", 2.5m)),
            Journal.Reversed(entryId, _april3));

        Assert.Equal((42.5m, 40m), await Backend.BalanceAsync(Backend.Entity, "1000"));
        Assert.Null(await Backend.BalanceAsync(Backend.Entity, "9999"));
    }

    [Fact]
    public async Task Un_echec_n_ecrit_ni_les_modeles_ni_le_checkpoint()
    {
        await Backend.DeliverAsync(Journal.Posted(_march2, Debit("1000", 40m), Credit("4000", 40m)));
        var before = await DumpAllAsync();
        var offsets = await ReadOffsetsAsync();
        var orphan = new JournalBuilder(Backend.Entity);
        var missing = Guid.CreateVersion7();
        orphan.Posted(missing, _march2, Debit("1000", 1m), Credit("4000", 1m));

        // Message livré sans être écrit dans le journal partagé : un rejeu complet ne doit pas le rencontrer.
        await Assert.ThrowsAsync<InvalidOperationException>(() => Backend.RedeliverAsync(orphan.Reversed(missing, _march20)));

        Assert.Equal(before, await DumpAllAsync());
        Assert.Equal(offsets, await ReadOffsetsAsync());
    }

    [Fact]
    public async Task Une_session_liberee_sans_validation_n_ecrit_rien()
    {
        var entry = (await Backend.RecordAllAsync([Journal.Posted(_march2, Debit("1000", 40m), Credit("4000", 40m))]))[0];
        var position = await Backend.PositionAsync(ProjectionNames.AccountBalances);
        var scope = Backend.Services.CreateAsyncScope();
        await using (scope)
        {
            var services = scope.ServiceProvider;
            await services.GetRequiredService<IProjectionSession>().BeginAsync(ProjectionTarget.Live, Ct);
            var context = EventContext.From(entry);
            Assert.True(await services.GetRequiredService<IAppliedEventLog>().TryMarkAppliedAsync(ProjectionNames.AccountBalances, context, Ct));
            await services.GetRequiredService<IAppliedEventLog>().AdvanceAsync(ProjectionNames.AccountBalances, context, Ct);
            await services.GetRequiredService<IAccountBalanceStore>().AddAsync(Backend.Entity, new AccountMovement("1000", Currency, 40m, 0m), Ct);
            await services.GetRequiredService<IConsumerCheckpoints>().SaveAsync(new SourcePosition(Backend.ConsumerGroup, ProjectionBackend.Topic, 0, 7), Ct);
        }

        Assert.Null(await Backend.BalanceAsync(Backend.Entity, "1000"));
        Assert.Empty(await ReadOffsetsAsync());
        Assert.DoesNotContain(entry.EventId.ToString(), await Backend.DumpAsync(ProjectionNames.AccountBalances, Backend.Entity), StringComparison.Ordinal);
        Assert.Equal(position, await Backend.PositionAsync(ProjectionNames.AccountBalances));
    }

    [Fact]
    public async Task Une_session_ne_peut_commencer_qu_une_fois()
    {
        var scope = Backend.Services.CreateAsyncScope();
        await using (scope)
        {
            var session = scope.ServiceProvider.GetRequiredService<IProjectionSession>();
            await session.BeginAsync(ProjectionTarget.Live, Ct);

            await Assert.ThrowsAsync<InvalidOperationException>(() => session.BeginAsync(ProjectionTarget.Live, Ct));
        }
    }

    [Fact]
    public async Task Le_checkpoint_est_le_prochain_offset_de_chaque_partition()
    {
        await Backend.DeliverAsync(Journal.Opened("2026-03"), Journal.Opened("2026-04"), Journal.Opened("2026-05"));

        Assert.Equal(new Dictionary<int, long> { [0] = 3 }, await ReadOffsetsAsync());
    }

    [Fact]
    public async Task La_position_ne_recule_pas_quand_un_evenement_arrive_apres_un_plus_recent()
    {
        var stored = await Backend.RecordAllAsync([Journal.Opened("2026-03"), Journal.Opened("2026-04")]);

        await Backend.RedeliverAsync(stored[1], stored[0]);

        Assert.Equal(stored[1].GlobalSequence, await Backend.PositionAsync(ProjectionNames.Periods));
        Assert.Equal(stored[1].GlobalSequence, await Backend.PositionAsync(ProjectionNames.ChartOfAccounts));
    }

    [Fact]
    public async Task Le_rejeu_rattrape_les_evenements_valides_mais_pas_encore_livres_sans_double_effet()
    {
        await Backend.DeliverAsync(Journal.Posted(_march2, Debit("1000", 10m), Credit("4000", 10m)));
        var pending = await Backend.RecordAllAsync([Journal.Posted(_march20, Debit("1000", 5m), Credit("4000", 5m))]);

        var report = await Backend.RebuildAsync(ProjectionNames.AccountBalances);
        Assert.Equal((15m, 0m), await Backend.BalanceAsync(Backend.Entity, "1000"));

        await Backend.RedeliverAsync([.. pending]);
        Assert.Equal((15m, 0m), await Backend.BalanceAsync(Backend.Entity, "1000"));
        Assert.True(report.Replayed >= 2);
    }

    [Fact]
    public async Task L_instantane_contient_une_ecriture_livree_apres_le_sceau()
    {
        var late = await Backend.RecordAllAsync([Journal.Opened("2026-03"), Journal.Posted(_march20, Debit("1100", 10m), Credit("4000", 10m))]);
        var seal = await Backend.DeliverAsync(Journal.Sealed("2026-03"));

        await Backend.RedeliverAsync([.. late]);

        var dump = await Backend.DumpAsync(ProjectionNames.PeriodSnapshots, Backend.Entity);
        Assert.Contains("1100", dump, StringComparison.Ordinal);
        Assert.Contains(seal[0].EventId.ToString(), dump, StringComparison.Ordinal);
        Assert.Equal(2, dump.Split('\n').Count(line => line.Contains("2026-03", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Exemple_de_reference_TPS_TVQ()
    {
        await Backend.DeliverAsync(
            Journal.Posted(_march2, Debit("1100", 114.98m), Credit("4000", 100m), Credit("2310", 5m), Credit("2320", 9.98m)),
            Journal.Posted(_march20, Debit("5100", 50m), Debit("1350", 2.50m), Debit("1360", 4.99m), Credit("2100", 57.49m)));

        Assert.Equal((5.00m, 2.50m, 9.98m, 4.99m), await TaxAsync("2026-03"));
    }

    [Fact]
    public async Task La_preparation_d_une_projection_inconnue_est_refusee()
    {
        var scope = Backend.Services.CreateAsyncScope();
        await using (scope)
        {
            await scope.ServiceProvider.GetRequiredService<IProjectionSession>().BeginAsync(ProjectionTarget.Rebuild("inconnue"), Ct);

            await Assert.ThrowsAsync<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<Application.Rebuild.IProjectionRebuildStore>().PrepareAsync("inconnue", Ct));
        }
    }

    protected abstract ProjectionBackend CreateBackend();

    /// <summary>TPS perçue, TPS payée, TVQ perçue, TVQ payée d'une période de l'entité.</summary>
    protected abstract Task<(decimal, decimal, decimal, decimal)?> TaxAsync(string period);

    private async Task<string> DumpBothAsync(string projection) =>
        await Backend.DumpAsync(projection, Backend.Entity) + "\n---\n" + await Backend.DumpAsync(projection, Backend.OtherEntity);

    private async Task<string> DumpAllAsync()
    {
        var dumps = new List<string>();
        foreach (var projection in ProjectionNames.All)
        {
            dumps.Add(await DumpBothAsync(projection));
        }

        return string.Join("\n", dumps);
    }

    private Task<IReadOnlyDictionary<int, long>> ReadOffsetsAsync() =>
        Backend.Services.GetRequiredService<IConsumerCheckpointReader>().ReadNextOffsetsAsync(Backend.ConsumerGroup, ProjectionBackend.Topic, Ct);
}
