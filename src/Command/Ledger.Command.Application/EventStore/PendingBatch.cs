using Ledger.Command.Application.Events;
using Ledger.Command.Domain;
using Ledger.SharedKernel;

namespace Ledger.Command.Application.EventStore;

/// <summary>
/// Événements décidés par une commande, tous de la même entité et de la même période. Ils ne
/// sont confiés à l'unité de travail qu'une fois toutes les décisions acceptées.
/// </summary>
internal sealed class PendingBatch(EventCatalog catalog, EntityId entity, DateOnly? businessDate, string period, EventMetadata metadata)
{
    private readonly List<EventToAppend> _events = [];

    public void Add(string streamId, long version, IDomainEvent domainEvent, decimal? balanceAfter = null, Guid? claimedEntryId = null)
    {
        var serialized = catalog.Serialize(domainEvent);
        _events.Add(new EventToAppend(
            Guid.CreateVersion7(),
            streamId,
            version,
            serialized.EventType,
            serialized.SchemaVersion,
            entity.Value,
            businessDate,
            period,
            serialized.Payload,
            balanceAfter,
            metadata,
            claimedEntryId));
    }

    /// <summary>Confie les événements à l'unité de travail et la valide.</summary>
    public Task<Result<IReadOnlyList<StoredEvent>>> CommitAsync(IEventAppender appender, IUnitOfWork unitOfWork, CancellationToken cancellationToken)
    {
        foreach (var pending in _events)
        {
            appender.Append(pending);
        }

        return unitOfWork.CommitAsync(cancellationToken);
    }
}
