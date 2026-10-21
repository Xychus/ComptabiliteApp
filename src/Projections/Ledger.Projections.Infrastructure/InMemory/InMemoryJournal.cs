using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Processing;
using Ledger.Projections.Application.Rebuild;

namespace Ledger.Projections.Infrastructure.InMemory;

/// <summary>Journal en mémoire (tests, développement) : enveloppes ajoutées dans l'ordre de leur séquence ou non.</summary>
public sealed class InMemoryJournal : IJournalReader
{
    private readonly Lock _gate = new();
    private readonly List<EventEnvelopeV1> _events = [];

    public void Append(EventEnvelopeV1 envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        lock (_gate)
        {
            _events.Add(envelope);
        }
    }

    public Task<IReadOnlyList<EventEnvelopeV1>> ReadAfterAsync(long afterSequence, int maxCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        return Task.FromResult(Select(e => e.GlobalSequence > afterSequence, maxCount));
    }

    public Task<IReadOnlyList<EventEnvelopeV1>> ReadPeriodEventsAsync(
        string entityId,
        string period,
        IReadOnlyCollection<string> eventTypes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);
        return Task.FromResult(Select(e => e.EntityId == entityId && e.Period == period && eventTypes.Contains(e.EventType)));
    }

    internal IReadOnlyList<EventEnvelopeV1> Select(Func<EventEnvelopeV1, bool> predicate, int maxCount = int.MaxValue)
    {
        lock (_gate)
        {
            return [.. _events.Where(predicate).OrderBy(e => e.GlobalSequence).Take(maxCount)];
        }
    }
}

/// <summary>Reconstruction en mémoire : mêmes étapes que PostgreSQL, sur les tables de la session.</summary>
public sealed class InMemoryProjectionRebuildStore(InMemoryProjectionSession session, InMemoryJournal journal) : IProjectionRebuildStore
{
    public Task PrepareAsync(string projection, CancellationToken cancellationToken)
    {
        foreach (var table in ProjectionTables.Of(projection))
        {
            session.Drop(table + ProjectionTables.RebuildSuffix);
        }

        var positions = session.PhysicalRows<string, ProjectionPosition>(ProjectionTables.Positions);
        session.SavePhysical(ProjectionTables.Positions, positions.Remove(ProjectionTables.Slot(projection, ProjectionTarget.Rebuild(projection))));
        return Task.CompletedTask;
    }

    /// <summary>Sans effet : les sessions en mémoire sont déjà sérialisées.</summary>
    public Task LockLiveAsync(string projection, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<EventEnvelopeV1>> ReadUnappliedAsync(string projection, IReadOnlyCollection<string> eventTypes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);
        var applied = session.PhysicalRows<Guid, long>(ProjectionTables.Applied(projection) + ProjectionTables.RebuildSuffix);
        return Task.FromResult(journal.Select(e => eventTypes.Contains(e.EventType) && !applied.ContainsKey(e.EventId)));
    }

    public Task SwapAsync(string projection, CancellationToken cancellationToken)
    {
        foreach (var table in ProjectionTables.Of(projection))
        {
            var rebuilt = session.PhysicalTable(table + ProjectionTables.RebuildSuffix);
            session.Drop(table);
            session.Drop(table + ProjectionTables.RebuildSuffix);
            if (rebuilt is not null)
            {
                session.SavePhysical(table, rebuilt);
            }
        }

        var positions = session.PhysicalRows<string, ProjectionPosition>(ProjectionTables.Positions);
        var live = ProjectionTables.Slot(projection, ProjectionTarget.Live);
        var rebuild = ProjectionTables.Slot(projection, ProjectionTarget.Rebuild(projection));
        positions = positions.Remove(live);
        if (positions.TryGetValue(rebuild, out var position))
        {
            positions = positions.Remove(rebuild).Add(live, position);
        }

        session.SavePhysical(ProjectionTables.Positions, positions);
        return Task.CompletedTask;
    }
}

/// <summary>Positions du consommateur dans la base en mémoire.</summary>
public sealed class InMemoryConsumerCheckpointReader(InMemoryProjectionDatabase database) : IConsumerCheckpointReader
{
    public Task<IReadOnlyDictionary<int, long>> ReadNextOffsetsAsync(string consumerGroup, string topic, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<int, long> offsets = database.Rows<ConsumerOffsetKey, long>(ProjectionTables.ConsumerOffsets)
            .Where(row => row.Key.ConsumerGroup == consumerGroup && row.Key.Topic == topic)
            .ToDictionary(row => row.Key.Partition, row => row.Value);
        return Task.FromResult(offsets);
    }
}
