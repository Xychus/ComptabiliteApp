using System.Text.Json;
using Ledger.Contracts.Events;
using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Processing;
using Ledger.Projections.Application.Upcasting;

namespace Ledger.Projections.Application.Events;

/// <summary>Événement décodé, prêt à être présenté aux projections qui le traitent.</summary>
public abstract class DecodedEvent(EventContext context)
{
    public EventContext Context { get; } = context;

    /// <summary>Vrai si <paramref name="projection"/> traite ce type d'événement.</summary>
    public abstract bool IsHandledBy(IProjection projection);

    /// <summary>Présente l'événement à la projection ; sans effet si elle ne le traite pas.</summary>
    public abstract Task DispatchToAsync(IProjection projection, CancellationToken cancellationToken);
}

internal sealed class DecodedEvent<T>(EventContext context, T data) : DecodedEvent(context)
    where T : class
{
    public override bool IsHandledBy(IProjection projection) => projection is IEventHandler<T>;

    public override Task DispatchToAsync(IProjection projection, CancellationToken cancellationToken) =>
        projection is IEventHandler<T> handler
            ? handler.HandleAsync(new ProjectedEvent<T>(Context, data), cancellationToken)
            : Task.CompletedTask;
}

/// <summary>Événement que le worker ne sait pas décoder : type inconnu, version sans upcaster, charge invalide.</summary>
public sealed class EventDecodingException : Exception
{
    public EventDecodingException()
    {
    }

    public EventDecodingException(string message)
        : base(message)
    {
    }

    public EventDecodingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Décode une enveloppe du journal : upcasting de la charge jusqu'à la version de schéma connue,
/// puis désérialisation dans le contrat correspondant. Un type inconnu ou une version plus
/// récente que celle du worker est une erreur : le worker s'arrête plutôt que d'ignorer une
/// écriture comptable (ADR 0015). Déployer d'abord la version qui le connaît.
/// </summary>
public sealed class EventDecoder
{
    private readonly Dictionary<string, Registration> _registrations;
    private readonly EventUpcasterChain _upcasters;

    private EventDecoder(Dictionary<string, Registration> registrations, EventUpcasterChain upcasters)
    {
        _registrations = registrations;
        _upcasters = upcasters;
    }

    /// <summary>Contrats V1 du journal, sans upcaster (tous les événements sont en version 1).</summary>
    public static EventDecoder Default { get; } = CreateBuilder().Build(EventUpcasterChain.Empty);

    /// <summary>Types d'événements du journal et leur contrat courant.</summary>
    public static Builder CreateBuilder() => new Builder()
        .Add<JournalEntryPostedV1>(EventTypes.JournalEntryPosted, EventSchema.Version)
        .Add<JournalEntryReversedV1>(EventTypes.JournalEntryReversed, EventSchema.Version)
        .Add<AccountLinePostedV1>(EventTypes.AccountLinePosted, EventSchema.Version)
        .Add<PeriodOpenedV1>(EventTypes.PeriodOpened, EventSchema.Version)
        .Add<PeriodSealedV1>(EventTypes.PeriodSealed, EventSchema.Version)
        .Add<AccountDefinedV1>(EventTypes.AccountDefined, EventSchema.Version);

    public DecodedEvent Decode(EventEnvelopeV1 envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (!_registrations.TryGetValue(envelope.EventType, out var registration))
        {
            throw new EventDecodingException($"Type d'événement inconnu : {envelope.EventType} (événement {envelope.EventId}).");
        }

        var payload = _upcasters.Upcast(envelope.EventType, envelope.SchemaVersion, registration.SchemaVersion, envelope.Payload);
        try
        {
            return registration.Create(EventContext.From(envelope), payload);
        }
        catch (JsonException exception)
        {
            throw new EventDecodingException($"Charge invalide pour {envelope.EventType} (événement {envelope.EventId}).", exception);
        }
    }

    /// <summary>Types d'événements que <paramref name="projection"/> traite (sélection du rejeu).</summary>
    public IReadOnlyList<string> EventTypesHandledBy(IProjection projection)
    {
        ArgumentNullException.ThrowIfNull(projection);
        return [.. _registrations.Where(r => r.Value.IsHandledBy(projection)).Select(r => r.Key).Order(StringComparer.Ordinal)];
    }

    private sealed record Registration(int SchemaVersion, Func<EventContext, JsonElement, DecodedEvent> Create, Func<IProjection, bool> IsHandledBy);

    /// <summary>Association type d'événement → contrat courant.</summary>
    public sealed class Builder
    {
        private readonly Dictionary<string, Registration> _registrations = new(StringComparer.Ordinal);

        /// <summary>Associe <paramref name="eventType"/> à son contrat <typeparamref name="T"/> en version <paramref name="schemaVersion"/>.</summary>
        public Builder Add<T>(string eventType, int schemaVersion)
            where T : class
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(eventType);
            ArgumentOutOfRangeException.ThrowIfLessThan(schemaVersion, 1);
            _registrations.Add(eventType, new Registration(
                schemaVersion,
                (context, payload) => new DecodedEvent<T>(context, payload.Deserialize<T>(EventJson.Options)
                    ?? throw new EventDecodingException($"Charge nulle pour {eventType} (événement {context.EventId}).")),
                projection => projection is IEventHandler<T>));
            return this;
        }

        public EventDecoder Build(EventUpcasterChain upcasters)
        {
            ArgumentNullException.ThrowIfNull(upcasters);
            return new EventDecoder(new Dictionary<string, Registration>(_registrations, StringComparer.Ordinal), upcasters);
        }
    }
}
