using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Events;
using Ledger.Projections.Application.Processing;
using Ledger.Projections.Application.Rebuild;
using Ledger.Projections.Application.Taxes;
using Ledger.Projections.Application.Tests.Support;
using Ledger.Projections.Infrastructure;
using Ledger.Projections.Infrastructure.InMemory;
using Ledger.Projections.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Ledger.Projections.Tests.Support.JournalBuilder;

namespace Ledger.Projections.Application.Tests.Rebuild;

/// <summary>Rejeu (ADR 0016) : vider puis rejouer une projection donne exactement le résultat incrémental.</summary>
public sealed class RebuildTests : IAsyncDisposable
{
    private static readonly TaxAccountMap _taxes = new(
    [
        new TaxAccount("ACME", "2310", TaxKind.GstCollected),
        new TaxAccount("ACME", "2320", TaxKind.QstCollected),
        new TaxAccount("ACME", "1350", TaxKind.GstPaid),
    ]);

    private readonly InMemoryProjections _projections = new(_taxes);

    public ValueTask DisposeAsync() => _projections.DisposeAsync();

    public static TheoryData<string> Projections => [.. ProjectionNames.All];

    [Theory]
    [MemberData(nameof(Projections))]
    public async Task Le_rejeu_reproduit_exactement_la_projection_incrementale(string projection)
    {
        await _projections.DeliverAsync(Scenario.Build());
        var incremental = _projections.Dump(projection);
        var position = Position(projection);

        var report = await _projections.RebuildAsync(projection);

        Assert.Equal(incremental, _projections.Dump(projection));
        Assert.Equal(position, Position(projection));
        Assert.Equal(new RebuildReport(projection, Scenario.Count, 0), report);
        Assert.DoesNotContain(_projections.Rows<string, ProjectionPosition>(ProjectionTables.Positions).Keys, slot => slot.EndsWith(ProjectionTables.RebuildSuffix, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Projections))]
    public async Task Le_rejeu_ne_touche_pas_aux_autres_projections(string projection)
    {
        await _projections.DeliverAsync(Scenario.Build());
        var others = ProjectionNames.All.Where(p => p != projection).ToDictionary(p => p, _projections.Dump);

        await _projections.RebuildAsync(projection);

        Assert.All(others, other => Assert.Equal(other.Value, _projections.Dump(other.Key)));
    }

    [Fact]
    public async Task Le_rejeu_d_une_projection_videe_la_reconstruit()
    {
        var events = Scenario.Build();
        await _projections.DeliverAsync(events);
        var expected = _projections.Dump(ProjectionNames.AccountBalances);
        await using var empty = new InMemoryProjections(_taxes);
        empty.Record(events);

        await empty.RebuildAsync(ProjectionNames.AccountBalances);

        Assert.Equal(expected, empty.Dump(ProjectionNames.AccountBalances));
    }

    [Fact]
    public async Task Les_evenements_valides_mais_pas_encore_livres_sont_rattrapes_sans_double_effet()
    {
        var acme = new JournalBuilder("ACME");
        var delivered = acme.Posted(new DateOnly(2026, 3, 2), Debit("1000", 10m), Credit("4000", 10m));
        var pending = acme.Posted(new DateOnly(2026, 3, 3), Debit("1000", 5m), Credit("4000", 5m));
        await _projections.DeliverAsync(delivered);
        _projections.Record(pending);

        await _projections.RebuildAsync(ProjectionNames.AccountBalances);
        await _projections.RedeliverAsync(pending);

        await using var reference = new InMemoryProjections(_taxes);
        await reference.DeliverAsync(delivered, pending);
        Assert.Equal(reference.Dump(ProjectionNames.AccountBalances), _projections.Dump(ProjectionNames.AccountBalances));
    }

    [Fact]
    public async Task Un_evenement_valide_hors_ordre_de_sequence_est_rattrape_sous_verrou()
    {
        // Séquences 1, 2 et 3 attribuées, 2 validée après la lecture du premier lot (curseur déjà à 3).
        var acme = new JournalBuilder("ACME");
        var first = acme.Posted(new DateOnly(2026, 3, 2), Debit("1000", 1m), Credit("4000", 1m));
        var straggler = acme.Posted(new DateOnly(2026, 3, 3), Debit("1000", 2m), Credit("4000", 2m));
        var third = acme.Posted(new DateOnly(2026, 3, 4), Debit("1000", 4m), Credit("4000", 4m));
        await _projections.DeliverAsync(first, third, straggler);
        var journal = new StragglerJournal(_projections.Journal, hiddenUntilSecondRead: straggler.EventId);
        var rebuilder = new ProjectionRebuilder(_projections.Services.GetRequiredService<IServiceScopeFactory>(), journal, EventDecoder.Default, NullLogger<ProjectionRebuilder>.Instance)
        {
            BatchSize = 10,
        };
        var incremental = _projections.Dump(ProjectionNames.AccountBalances);

        var report = await rebuilder.RebuildAsync(ProjectionNames.AccountBalances, TestContext.Current.CancellationToken);

        Assert.Equal(new RebuildReport(ProjectionNames.AccountBalances, 2, 1), report);
        Assert.Equal(incremental, _projections.Dump(ProjectionNames.AccountBalances));
    }

    [Fact]
    public async Task Le_rejeu_lit_le_journal_par_lots()
    {
        await _projections.DeliverAsync(Scenario.Build());
        var rebuilder = new ProjectionRebuilder(_projections.Services.GetRequiredService<IServiceScopeFactory>(), _projections.Journal, EventDecoder.Default, NullLogger<ProjectionRebuilder>.Instance)
        {
            BatchSize = 2,
        };
        var incremental = _projections.Dump(ProjectionNames.LedgerEntries);

        var report = await rebuilder.RebuildAsync(ProjectionNames.LedgerEntries, TestContext.Current.CancellationToken);

        Assert.Equal(Scenario.Count, report.Replayed);
        Assert.Equal(incremental, _projections.Dump(ProjectionNames.LedgerEntries));
    }

    [Fact]
    public async Task Une_projection_inconnue_est_refusee() =>
        await Assert.ThrowsAsync<ArgumentException>(() => _projections.RebuildAsync("inconnue"));

    [Fact]
    public async Task Le_rejeu_d_un_journal_vide_donne_une_projection_vide()
    {
        var report = await _projections.RebuildAsync(ProjectionNames.Periods);

        Assert.Equal(new RebuildReport(ProjectionNames.Periods, 0, 0), report);
        Assert.Empty(_projections.Rows<PeriodKey, PeriodRow>(ProjectionTables.Periods));
    }

    private ProjectionPosition? Position(string projection) =>
        _projections.Rows<string, ProjectionPosition>(ProjectionTables.Positions).GetValueOrDefault(projection);

    /// <summary>Journal où un événement n'est visible qu'à partir de la seconde lecture (validation tardive).</summary>
    private sealed class StragglerJournal(InMemoryJournal journal, Guid hiddenUntilSecondRead) : IJournalReader
    {
        private int _reads;

        public async Task<IReadOnlyList<EventEnvelopeV1>> ReadAfterAsync(long afterSequence, int maxCount, CancellationToken cancellationToken)
        {
            var events = await journal.ReadAfterAsync(afterSequence, maxCount, cancellationToken);
            return _reads++ == 0 ? [.. events.Where(e => e.EventId != hiddenUntilSecondRead)] : events;
        }

        public Task<IReadOnlyList<EventEnvelopeV1>> ReadPeriodEventsAsync(string entityId, string period, IReadOnlyCollection<string> eventTypes, CancellationToken cancellationToken) =>
            journal.ReadPeriodEventsAsync(entityId, period, eventTypes, cancellationToken);
    }
}
