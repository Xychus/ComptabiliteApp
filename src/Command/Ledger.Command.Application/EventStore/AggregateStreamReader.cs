using Ledger.Command.Application.Events;
using Ledger.Command.Domain;

namespace Ledger.Command.Application.EventStore;

/// <summary>État d'un agrégat reconstruit depuis son flux, avec la version courante du flux.</summary>
public sealed record StreamSnapshot<TState>(TState State, long Version, IReadOnlyList<StoredEvent> Events);

/// <summary>Reconstruit l'état d'un agrégat en rejouant son flux (lecture du journal, jamais d'une projection).</summary>
public sealed class AggregateStreamReader(IEventReader reader, EventCatalog catalog)
{
    public async Task<StreamSnapshot<TState>> LoadAsync<TState>(
        string streamId,
        TState initial,
        Func<TState, IDomainEvent, TState> evolve,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evolve);
        var events = await reader.ReadStreamAsync(streamId, cancellationToken).ConfigureAwait(false);
        var state = events.Aggregate(initial, (current, stored) => evolve(current, catalog.Deserialize(stored.EventType, stored.SchemaVersion, stored.Payload)));
        return new StreamSnapshot<TState>(state, events.Count == 0 ? 0 : events[^1].StreamVersion, events);
    }
}
