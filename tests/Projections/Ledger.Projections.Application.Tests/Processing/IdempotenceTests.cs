using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Events;
using Ledger.Projections.Application.Processing;
using Ledger.Projections.Application.Tests.Support;
using Ledger.Projections.Infrastructure;
using Ledger.Projections.Infrastructure.InMemory;
using Ledger.Projections.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static Ledger.Projections.Tests.Support.JournalBuilder;

namespace Ledger.Projections.Application.Tests.Processing;

/// <summary>Consommateurs idempotents (invariant 7) et checkpoint transactionnel (ADR 0015).</summary>
public sealed class IdempotenceTests : IAsyncDisposable
{
    private static readonly DateOnly _march2 = new(2026, 3, 2);

    private readonly InMemoryProjections _projections = new();
    private readonly JournalBuilder _acme = new("ACME");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask DisposeAsync() => _projections.DisposeAsync();

    [Fact]
    public async Task Le_meme_evenement_livre_deux_fois_n_a_qu_un_effet()
    {
        var sale = _acme.Posted(_march2, Debit("1000", 40m), Credit("4000", 40m));
        await _projections.DeliverAsync(_acme.Opened("2026-03"), sale);
        var once = AllDumps();

        await _projections.RedeliverAsync(sale);
        await _projections.RedeliverAsync(sale);

        Assert.Equal(once, AllDumps());
        Assert.Equal(new BalanceRow(40m, 0m), _projections.Rows<BalanceKey, BalanceRow>(ProjectionTables.AccountBalances)[new BalanceKey("ACME", "1000", Currency)]);
    }

    [Fact]
    public async Task Chaque_projection_marque_les_seuls_evenements_qu_elle_traite()
    {
        await _projections.DeliverAsync(_acme.Opened("2026-03"), _acme.Posted(_march2, Debit("1000", 1m), Credit("4000", 1m)));

        Assert.Single(_projections.Rows<Guid, long>(ProjectionTables.Applied(ProjectionNames.AccountBalances)));
        Assert.Single(_projections.Rows<Guid, long>(ProjectionTables.Applied(ProjectionNames.Periods)));
        Assert.Empty(_projections.Rows<Guid, long>(ProjectionTables.Applied(ProjectionNames.ChartOfAccounts)));
    }

    [Fact]
    public async Task Le_checkpoint_est_le_prochain_offset_de_la_partition()
    {
        await _projections.DeliverAsync(_acme.Opened("2026-03"), _acme.Opened("2026-04"), _acme.Opened("2026-05"));

        var next = await _projections.Services.GetRequiredService<IConsumerCheckpointReader>()
            .ReadNextOffsetsAsync(InMemoryProjections.ConsumerGroup, InMemoryProjections.Topic, Ct);

        Assert.Equal(new Dictionary<int, long> { [0] = 3 }, next);
        Assert.Empty(await _projections.Services.GetRequiredService<IConsumerCheckpointReader>().ReadNextOffsetsAsync("autre-groupe", InMemoryProjections.Topic, Ct));
    }

    [Fact]
    public async Task La_position_de_chaque_projection_avance_meme_sans_evenement_traite()
    {
        var opened = _acme.Opened("2026-03");
        var last = _acme.AccountDefined("1000", "Encaisse");
        await _projections.DeliverAsync(opened, last);

        var positions = _projections.Rows<string, ProjectionPosition>(ProjectionTables.Positions);

        Assert.Equal(ProjectionNames.All.Order(StringComparer.Ordinal), positions.Keys.Order(StringComparer.Ordinal));
        Assert.All(positions.Values, position => Assert.Equal(new ProjectionPosition(last.GlobalSequence, last.RecordedAt), position));
    }

    [Fact]
    public async Task Une_position_ne_recule_pas_quand_un_evenement_valide_hors_ordre_arrive_apres()
    {
        var later = _acme.Opened("2026-04");
        _acme.NextSequence = 1;
        var earlier = _acme.Opened("2026-03");

        await _projections.DeliverAsync(later, earlier);

        Assert.Equal(later.GlobalSequence, _projections.Rows<string, ProjectionPosition>(ProjectionTables.Positions)[ProjectionNames.Periods].ThroughSequence);
        Assert.Equal(2, _projections.Rows<PeriodKey, PeriodRow>(ProjectionTables.Periods).Count);
    }

    [Fact]
    public async Task Un_echec_n_ecrit_ni_les_modeles_ni_le_checkpoint()
    {
        await _projections.DeliverAsync(_acme.Opened("2026-03"));
        var before = AllDumps();
        var offsets = _projections.Rows<ConsumerOffsetKey, long>(ProjectionTables.ConsumerOffsets);
        var orphan = new JournalBuilder("ACME");
        orphan.Posted(Guid.Empty, _march2, Debit("1000", 1m), Credit("4000", 1m));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _projections.DeliverAsync(orphan.Reversed(Guid.Empty, _march2)));

        Assert.Equal(before, AllDumps());
        Assert.Equal(offsets, _projections.Rows<ConsumerOffsetKey, long>(ProjectionTables.ConsumerOffsets));
    }

    [Fact]
    public async Task Un_evenement_indecodable_arrete_la_boucle_avant_toute_ecriture()
    {
        var bad = _acme.Opened("2026-03") with { EventType = "Inconnu" };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var source = new QueueSource(stop, bad);
        var pump = new ProjectionPump(_projections.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ProjectionPump>.Instance);

        await Assert.ThrowsAsync<EventDecodingException>(() => pump.RunAsync(source, stop.Token));

        Assert.Empty(source.Acknowledged);
        Assert.Empty(_projections.Rows<ConsumerOffsetKey, long>(ProjectionTables.ConsumerOffsets));
    }

    [Fact]
    public async Task La_boucle_traite_puis_acquitte_chaque_message_jusqu_a_l_annulation()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var source = new QueueSource(stop, _acme.Opened("2026-03"), _acme.Opened("2026-04"));
        var pump = new ProjectionPump(_projections.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ProjectionPump>.Instance);

        await pump.RunAsync(source, stop.Token);

        Assert.Equal([0L, 1L], source.Acknowledged.Select(message => message.Position.Offset));
        Assert.Equal(2, _projections.Rows<PeriodKey, PeriodRow>(ProjectionTables.Periods).Count);
    }

    [Fact]
    public async Task La_boucle_s_arrete_sans_erreur_si_elle_est_annulee()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var pump = new ProjectionPump(_projections.Services.GetRequiredService<IServiceScopeFactory>(), NullLogger<ProjectionPump>.Instance);

        await pump.RunAsync(new QueueSource(cancellation), cancellation.Token);
    }

    private string AllDumps() => string.Join("\n", ProjectionNames.All.Select(_projections.Dump));

    /// <summary>Source qui livre ses messages puis demande l'arrêt du worker une fois vide.</summary>
    private sealed class QueueSource(CancellationTokenSource stop, params EventEnvelopeV1[] events) : IEventSource
    {
        private readonly Queue<EventEnvelopeV1> _pending = new(events);
        private long _offset;

        public List<SourceMessage> Acknowledged { get; } = [];

        public SourceMessage Consume(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_pending.Count == 0)
            {
                stop.Cancel();
                cancellationToken.ThrowIfCancellationRequested();
            }

            return new SourceMessage(_pending.Dequeue(), new SourcePosition(InMemoryProjections.ConsumerGroup, InMemoryProjections.Topic, 0, _offset++));
        }

        public void Acknowledge(SourceMessage message) => Acknowledged.Add(message);
    }
}
