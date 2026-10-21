using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Entries;
using Ledger.Projections.Application.Events;
using Ledger.Projections.Application.Processing;
using Ledger.Projections.Application.Rebuild;

namespace Ledger.Projections.Application.Snapshots;

/// <summary>Mouvements scellés d'un compte dans la période.</summary>
public sealed record SnapshotLine(string AccountId, string Currency, decimal Debits, decimal Credits);

/// <summary>Instantané de fin de période, figé au scellement (ADR 0017).</summary>
/// <param name="EntityId">Entité comptable.</param>
/// <param name="Period">Période scellée (AAAA-MM).</param>
/// <param name="SealSequence">Séquence du <c>PeriodSealed</c> qui fige l'instantané.</param>
/// <param name="EventCount">Nombre d'événements scellés, repris du sceau.</param>
/// <param name="Lines">Mouvements par compte et devise, triés par compte.</param>
public sealed record PeriodSnapshot(string EntityId, string Period, long SealSequence, long EventCount, IReadOnlyList<SnapshotLine> Lines);

/// <summary>Port d'écriture de <c>period_snapshots</c>.</summary>
public interface IPeriodSnapshotStore
{
    /// <summary>Enregistre l'instantané ; il n'est jamais modifié ensuite (la période est scellée).</summary>
    Task SaveAsync(PeriodSnapshot snapshot, CancellationToken cancellationToken);
}

/// <summary>
/// Instantanés de fin de période : au <c>PeriodSealed</c>, les mouvements de la période sont
/// recalculés depuis la table <c>events</c>, pas depuis une autre projection. Le sceau garantit
/// que la période est complète et le restera (invariant 8) : l'instantané ne dépend ni du retard
/// des projections ni de l'ordre de traitement, et le rejeu le reproduit à l'identique.
/// </summary>
public sealed class PeriodSnapshotsProjection(IJournalReader journal, EventDecoder decoder, IPeriodSnapshotStore store)
    : IProjection, IEventHandler<PeriodSealedV1>
{
    private static readonly string[] _entryEventTypes = [EventTypes.JournalEntryPosted, EventTypes.JournalEntryReversed];

    public string Name => ProjectionNames.PeriodSnapshots;

    public async Task HandleAsync(ProjectedEvent<PeriodSealedV1> projectedEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectedEvent);
        var data = projectedEvent.Data;
        var events = await journal.ReadPeriodEventsAsync(data.EntityId, data.Period, _entryEventTypes, cancellationToken).ConfigureAwait(false);
        var collector = new MovementCollector();
        foreach (var envelope in events)
        {
            await decoder.Decode(envelope).DispatchToAsync(collector, cancellationToken).ConfigureAwait(false);
        }

        await store.SaveAsync(
            new PeriodSnapshot(data.EntityId, data.Period, projectedEvent.Context.GlobalSequence, data.EventCount, collector.Lines()),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cumule les lignes des écritures de la période, par compte et devise.</summary>
    private sealed class MovementCollector : IProjection, IEventHandler<JournalEntryPostedV1>, IEventHandler<JournalEntryReversedV1>
    {
        private readonly SortedDictionary<(string Account, string Currency), (decimal Debits, decimal Credits)> _totals = [];

        public string Name => ProjectionNames.PeriodSnapshots;

        public Task HandleAsync(ProjectedEvent<JournalEntryPostedV1> projectedEvent, CancellationToken cancellationToken)
        {
            Add(projectedEvent.Data.Lines);
            return Task.CompletedTask;
        }

        public Task HandleAsync(ProjectedEvent<JournalEntryReversedV1> projectedEvent, CancellationToken cancellationToken)
        {
            Add(projectedEvent.Data.Lines);
            return Task.CompletedTask;
        }

        public IReadOnlyList<SnapshotLine> Lines() =>
            [.. _totals.Select(total => new SnapshotLine(total.Key.Account, total.Key.Currency, total.Value.Debits, total.Value.Credits))];

        private void Add(IEnumerable<JournalLineV1> lines)
        {
            foreach (var movement in JournalLines.Movements(lines))
            {
                var key = (movement.AccountId, movement.Currency);
                var total = _totals.GetValueOrDefault(key);
                _totals[key] = (total.Debits + movement.Debit, total.Credits + movement.Credit);
            }
        }
    }
}
