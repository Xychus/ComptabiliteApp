using Ledger.Command.Application.EventStore;
using Ledger.Command.Infrastructure.EventStore;
using Ledger.Command.Infrastructure.Outbox;
using Ledger.Contracts.Events.V1;
using Ledger.SharedKernel;

namespace Ledger.Command.Infrastructure.InMemory;

/// <summary>
/// Event store en mémoire, pour les tests et le développement. Il passe la même suite de contrat
/// que l'implémentation PostgreSQL (append-only, versions, réservation d'identifiants,
/// chaînage, période scellée, outbox atomique). Un verrou global sérialise les validations.
/// </summary>
public sealed class InMemoryEventStore(IClock clock) : IEventReader
{
    private readonly Lock _gate = new();
    private readonly List<StoredEvent> _events = [];
    private readonly Dictionary<string, List<StoredEvent>> _streams = new(StringComparer.Ordinal);
    private readonly HashSet<Guid> _eventIds = [];
    private readonly HashSet<Guid> _claimedEntryIds = [];
    private readonly HashSet<(string Entity, string Period)> _sealedPeriods = [];
    private readonly List<OutboxMessage> _outbox = [];

    public IReadOnlyList<OutboxMessage> Outbox
    {
        get
        {
            lock (_gate)
            {
                return [.. _outbox];
            }
        }
    }

    public InMemoryUnitOfWork BeginUnitOfWork() => new(this);

    public Task<IReadOnlyList<StoredEvent>> ReadStreamAsync(string streamId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<StoredEvent> events = _streams.TryGetValue(streamId, out var stream) ? [.. stream] : [];
            return Task.FromResult(events);
        }
    }

    internal Result<IReadOnlyList<StoredEvent>> Commit(IReadOnlyList<EventToAppend> events)
    {
        lock (_gate)
        {
            var heads = events.Select(e => e.StreamId).Distinct(StringComparer.Ordinal)
                .Where(_streams.ContainsKey)
                .ToDictionary(id => id, id => new StreamHead(_streams[id][^1].StreamVersion, _streams[id][^1].Hash), StringComparer.Ordinal);
            var prepared = AppendBatch.Prepare(events, heads, clock.UtcNow);
            if (prepared.IsFailure)
            {
                return prepared;
            }

            if (Rejection(events) is { } rejection)
            {
                return Result.Failure<IReadOnlyList<StoredEvent>>(rejection);
            }

            var stored = prepared.Value.Select((e, index) => e with { GlobalSequence = _events.Count + index + 1 }).ToList();
            for (var index = 0; index < stored.Count; index++)
            {
                Apply(stored[index], events[index].ClaimedEntryId);
            }

            return Result.Success<IReadOnlyList<StoredEvent>>(stored);
        }
    }

    /// <summary>
    /// Contrôles que PostgreSQL fait, dans le même ordre : le trigger de période scellée à
    /// l'insertion des événements, puis les contraintes d'unicité des tables compagnes.
    /// </summary>
    private DomainError? Rejection(IReadOnlyList<EventToAppend> events)
    {
        var sealedPeriods = new HashSet<(string, string)>(_sealedPeriods);
        foreach (var candidate in events)
        {
            if (sealedPeriods.Contains((candidate.EntityId, candidate.Period)))
            {
                return EventStoreErrors.PeriodSealed(candidate.EntityId, candidate.Period);
            }

            if (candidate.EventType == EventTypes.PeriodSealed)
            {
                sealedPeriods.Add((candidate.EntityId, candidate.Period));
            }
        }

        foreach (var candidate in events)
        {
            if (_eventIds.Contains(candidate.EventId))
            {
                return EventStoreErrors.ConcurrencyConflict(candidate.StreamId, candidate.StreamVersion);
            }

            if (candidate.ClaimedEntryId is { } claimed && _claimedEntryIds.Contains(claimed))
            {
                return EventStoreErrors.EntryIdAlreadyUsed(claimed);
            }
        }

        return null;
    }

    private void Apply(StoredEvent stored, Guid? claimedEntryId)
    {
        _events.Add(stored);
        _eventIds.Add(stored.EventId);
        if (!_streams.TryGetValue(stored.StreamId, out var stream))
        {
            stream = [];
            _streams.Add(stored.StreamId, stream);
        }

        stream.Add(stored);
        if (claimedEntryId is { } claimed)
        {
            _claimedEntryIds.Add(claimed);
        }

        if (stored.EventType == EventTypes.PeriodSealed)
        {
            _sealedPeriods.Add((stored.EntityId, stored.Period));
        }

        _outbox.Add(OutboxMessages.From(stored));
    }
}

/// <summary>Unité de travail sur <see cref="InMemoryEventStore"/> ; une instance par commande.</summary>
public sealed class InMemoryUnitOfWork(InMemoryEventStore store) : IEventAppender, IUnitOfWork
{
    private readonly List<EventToAppend> _staged = [];

    public void Append(EventToAppend eventToAppend)
    {
        ArgumentNullException.ThrowIfNull(eventToAppend);
        _staged.Add(eventToAppend);
    }

    public Task<Result<IReadOnlyList<StoredEvent>>> CommitAsync(CancellationToken cancellationToken)
    {
        EventToAppend[] batch = [.. _staged];
        _staged.Clear();
        return Task.FromResult(store.Commit(batch));
    }
}
