using System.Globalization;
using System.Text.Json;
using Ledger.Command.Application.EventStore;
using Ledger.Contracts.Events;
using Ledger.Contracts.Events.V1;

namespace Ledger.Command.Infrastructure.Outbox;

/// <summary>Ligne de la table <c>outbox</c>, lue par Debezium (ADR 0007).</summary>
public sealed record OutboxMessage(Guid EventId, string Topic, string PartitionKey, JsonElement Payload);

/// <summary>Construit la ligne d'outbox d'un événement stocké : enveloppe V1, sujet unique, clé = entité.</summary>
public static class OutboxMessages
{
    public const string Topic = "ledger.events";

    public static OutboxMessage From(StoredEvent stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return new OutboxMessage(stored.EventId, Topic, stored.EntityId, JsonSerializer.SerializeToElement(ToEnvelope(stored), EventJson.Options));
    }

    public static EventEnvelopeV1 ToEnvelope(StoredEvent stored)
    {
        ArgumentNullException.ThrowIfNull(stored);
        return new EventEnvelopeV1(
            stored.GlobalSequence,
            stored.EventId,
            stored.StreamId,
            stored.StreamVersion,
            stored.EventType,
            stored.SchemaVersion,
            stored.EntityId,
            stored.BusinessDate,
            stored.RecordedAt,
            stored.Period,
            stored.BalanceAfter?.ToString("0.00", CultureInfo.InvariantCulture),
            stored.Metadata.CorrelationId,
            stored.Metadata.CausationId,
            stored.Metadata.Actor,
            stored.Hash,
            stored.PreviousHash,
            stored.Payload);
    }
}
