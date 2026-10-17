using System.Text.Json;
using Ledger.Contracts.Integrity;

namespace Ledger.Command.Application.EventStore;

/// <summary>Contexte de la commande qui a produit l'événement.</summary>
public sealed record EventMetadata(Guid CorrelationId, Guid? CausationId, string Actor);

/// <summary>
/// Événement à ajouter au journal. Ne porte aucun champ d'autorité serveur : la séquence
/// globale, l'horodatage et le hash sont attribués par l'event store (invariant 5).
/// <see cref="StreamVersion"/> est la version visée (version courante du flux + 1, concurrence optimiste) ;
/// <see cref="BalanceAfter"/>, le solde après la ligne d'un compte contraint (ADR 0004) ;
/// <see cref="ClaimedEntryId"/>, l'identifiant d'écriture que l'événement réserve dans tout le journal (ADR 0003).
/// </summary>
public sealed record EventToAppend(
    Guid EventId,
    string StreamId,
    long StreamVersion,
    string EventType,
    int SchemaVersion,
    string EntityId,
    DateOnly? BusinessDate,
    string Period,
    JsonElement Payload,
    decimal? BalanceAfter,
    EventMetadata Metadata,
    Guid? ClaimedEntryId = null);

/// <summary>Événement tel que stocké dans le journal, avec les champs attribués par le serveur.</summary>
public sealed record StoredEvent(
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
    JsonElement Payload,
    decimal? BalanceAfter,
    EventMetadata Metadata,
    string Hash,
    string? PreviousHash)
{
    /// <summary>Champs couverts par le hash (ADR 0006), pour le calculer ou le vérifier.</summary>
    public EventHashInput ToHashInput()
    {
        ArgumentNullException.ThrowIfNull(Metadata);
        return new(
            EventId,
            StreamId,
            StreamVersion,
            EventType,
            SchemaVersion,
            EntityId,
            BusinessDate,
            RecordedAt,
            Period,
            Payload,
            BalanceAfter,
            Metadata.CorrelationId,
            Metadata.CausationId,
            Metadata.Actor,
            PreviousHash);
    }
}
