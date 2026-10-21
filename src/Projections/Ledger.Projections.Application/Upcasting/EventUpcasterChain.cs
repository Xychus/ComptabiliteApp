using System.Text.Json;
using Ledger.Projections.Application.Events;

namespace Ledger.Projections.Application.Upcasting;

/// <summary>
/// Transforme la charge d'un type d'événement de la version <see cref="FromVersion"/> à la
/// version suivante. Les événements stockés ne changent jamais : la conversion a lieu à chaque
/// lecture (CLAUDE.md : un changement de schéma passe par l'upcasting).
/// </summary>
public interface IEventUpcaster
{
    string EventType { get; }

    int FromVersion { get; }

    JsonElement Upcast(JsonElement payload);
}

/// <summary>
/// Chaîne d'upcasters : une charge en version <c>n</c> passe par les upcasters <c>n → n+1</c>,
/// <c>n+1 → n+2</c>… jusqu'à la version courante du contrat. Un maillon manquant est une erreur.
/// </summary>
public sealed class EventUpcasterChain
{
    private readonly Dictionary<(string EventType, int FromVersion), IEventUpcaster> _upcasters = [];

    public EventUpcasterChain(IEnumerable<IEventUpcaster> upcasters)
    {
        ArgumentNullException.ThrowIfNull(upcasters);
        foreach (var upcaster in upcasters)
        {
            if (!_upcasters.TryAdd((upcaster.EventType, upcaster.FromVersion), upcaster))
            {
                throw new ArgumentException($"Deux upcasters pour {upcaster.EventType} v{upcaster.FromVersion}.", nameof(upcasters));
            }
        }
    }

    /// <summary>Aucun upcaster : tous les événements sont dans leur version courante (V1).</summary>
    public static EventUpcasterChain Empty { get; } = new([]);

    public JsonElement Upcast(string eventType, int fromVersion, int toVersion, JsonElement payload)
    {
        if (fromVersion > toVersion)
        {
            throw new EventDecodingException($"{eventType} v{fromVersion} est plus récent que la version connue du worker (v{toVersion}).");
        }

        for (var version = fromVersion; version < toVersion; version++)
        {
            if (!_upcasters.TryGetValue((eventType, version), out var upcaster))
            {
                throw new EventDecodingException($"Aucun upcaster de {eventType} v{version} vers v{version + 1}.");
            }

            payload = upcaster.Upcast(payload);
        }

        return payload;
    }
}
