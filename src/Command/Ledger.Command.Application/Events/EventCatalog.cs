using System.Text.Json;
using Ledger.Command.Domain;
using Ledger.Command.Domain.Accounts;
using Ledger.Command.Domain.ConstrainedAccounts;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Periods;
using Ledger.Contracts.Events;
using Ledger.Contracts.Events.V1;

namespace Ledger.Command.Application.Events;

/// <summary>Charge sérialisée d'un événement de domaine, prête à être stockée.</summary>
public sealed record SerializedEvent(string EventType, int SchemaVersion, JsonElement Payload);

/// <summary>Correspondance entre un type d'événement de domaine et son contrat stocké.</summary>
public interface IEventMapping
{
    Type DomainType { get; }

    string EventType { get; }

    JsonElement Serialize(IDomainEvent domainEvent);

    IDomainEvent Deserialize(JsonElement payload);
}

/// <summary>Correspondance typée : conversion domaine ↔ contrat, puis contrat ↔ JSON.</summary>
public sealed class EventMapping<TEvent, TContract>(string eventType, Func<TEvent, TContract> toContract, Func<TContract, TEvent> toDomain) : IEventMapping
    where TEvent : IDomainEvent
{
    public Type DomainType => typeof(TEvent);

    public string EventType => eventType;

    public JsonElement Serialize(IDomainEvent domainEvent) =>
        JsonSerializer.SerializeToElement(toContract((TEvent)domainEvent), EventJson.Options);

    public IDomainEvent Deserialize(JsonElement payload) =>
        toDomain(payload.Deserialize<TContract>(EventJson.Options)
            ?? throw new InvalidOperationException($"Charge vide pour un événement {eventType}."));
}

/// <summary>
/// Catalogue des événements stockés. Ajouter un type d'événement = enregistrer une correspondance.
/// Seule la version de schéma courante est lue ici ; l'upcasting des versions antérieures
/// s'ajoutera devant <see cref="Deserialize"/> (phase 4).
/// </summary>
public sealed class EventCatalog
{
    private readonly Dictionary<Type, IEventMapping> _byDomainType;
    private readonly Dictionary<string, IEventMapping> _byEventType;

    public EventCatalog(IEnumerable<IEventMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var all = mappings.ToList();
        _byDomainType = all.ToDictionary(m => m.DomainType);
        _byEventType = all.ToDictionary(m => m.EventType, StringComparer.Ordinal);
    }

    public static EventCatalog Default { get; } = new(
    [
        new EventMapping<JournalEntryPosted, JournalEntryPostedV1>(EventTypes.JournalEntryPosted, EventContractMapper.ToContract, EventContractMapper.ToDomain),
        new EventMapping<JournalEntryReversed, JournalEntryReversedV1>(EventTypes.JournalEntryReversed, EventContractMapper.ToContract, EventContractMapper.ToDomain),
        new EventMapping<AccountLinePosted, AccountLinePostedV1>(EventTypes.AccountLinePosted, EventContractMapper.ToContract, EventContractMapper.ToDomain),
        new EventMapping<PeriodOpened, PeriodOpenedV1>(EventTypes.PeriodOpened, EventContractMapper.ToContract, EventContractMapper.ToDomain),
        new EventMapping<PeriodSealed, PeriodSealedV1>(EventTypes.PeriodSealed, EventContractMapper.ToContract, EventContractMapper.ToDomain),
        new EventMapping<AccountDefined, AccountDefinedV1>(EventTypes.AccountDefined, EventContractMapper.ToContract, EventContractMapper.ToDomain),
    ]);

    public SerializedEvent Serialize(IDomainEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        var mapping = _byDomainType.GetValueOrDefault(@event.GetType())
            ?? throw new InvalidOperationException($"Aucun contrat pour l'événement {@event.GetType().Name}.");
        return new SerializedEvent(mapping.EventType, EventSchema.Version, mapping.Serialize(@event));
    }

    public IDomainEvent Deserialize(string eventType, int schemaVersion, JsonElement payload)
    {
        var mapping = _byEventType.GetValueOrDefault(eventType)
            ?? throw new InvalidOperationException($"Type d'événement stocké inconnu : « {eventType} ».");
        if (schemaVersion != EventSchema.Version)
        {
            throw new NotSupportedException($"Version de schéma {schemaVersion} de {eventType} non prise en charge (upcasting à venir).");
        }

        return mapping.Deserialize(payload);
    }
}
