using System.Text.Json;

namespace Ledger.Contracts.Events.V1;

/// <summary>
/// Enveloppe d'un événement stocké, telle que publiée par l'outbox (ADR 0007) : métadonnées
/// attribuées par le serveur, hash chaîné et charge (<see cref="Payload"/>, contrat de
/// <see cref="EventType"/> en version <see cref="SchemaVersion"/>).
/// <see cref="BalanceAfter"/> est une chaîne décimale, comme tous les montants des contrats.
/// </summary>
public sealed record EventEnvelopeV1(
    long GlobalSequence,
    Guid EventId,
    string StreamId,
    long StreamVersion,
    string EventType,
    int SchemaVersion,
    string EntityId,
    DateOnly? BusinessDate,
    DateTimeOffset RecordedAt,
    string Period,
    string? BalanceAfter,
    Guid CorrelationId,
    Guid? CausationId,
    string Actor,
    string Hash,
    string? PreviousHash,
    JsonElement Payload);
