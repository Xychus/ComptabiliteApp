using System.Diagnostics.CodeAnalysis;
using Ledger.Contracts.Events.V1;

namespace Ledger.Projections.Application.Events;

/// <summary>
/// Métadonnées d'un événement du journal, attribuées par le serveur (ordre, horodatage, période).
/// Une projection n'en tire que des données dérivées : jamais l'horloge du worker.
/// </summary>
public sealed record EventContext(
    long GlobalSequence,
    Guid EventId,
    string StreamId,
    long StreamVersion,
    string EventType,
    string EntityId,
    DateOnly? BusinessDate,
    DateTimeOffset RecordedAt,
    string Period,
    string Actor)
{
    public static EventContext From(EventEnvelopeV1 envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return new(
            envelope.GlobalSequence,
            envelope.EventId,
            envelope.StreamId,
            envelope.StreamVersion,
            envelope.EventType,
            envelope.EntityId,
            envelope.BusinessDate,
            envelope.RecordedAt,
            envelope.Period,
            envelope.Actor);
    }
}

/// <summary>Événement décodé (après upcasting) : contrat de la dernière version de schéma et métadonnées.</summary>
public sealed record ProjectedEvent<T>(EventContext Context, T Data)
    where T : class;

/// <summary>
/// Réaction d'une projection à un type d'événement. Une projection implémente cette interface
/// pour chaque type qui la concerne ; les autres types ne lui sont jamais présentés.
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix", Justification = "Nom du port imposé par CLAUDE.md (extension par ajout de IEventHandler<T>) ; ce n'est pas un délégué d'événement .NET.")]
public interface IEventHandler<T>
    where T : class
{
    Task HandleAsync(ProjectedEvent<T> projectedEvent, CancellationToken cancellationToken);
}
