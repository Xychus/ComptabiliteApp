using System.Text.Json;

namespace Ledger.Contracts.Events;

/// <summary>
/// Options de sérialisation des événements (charge stockée en <c>JSONB</c> et enveloppe publiée) :
/// propriétés en camelCase. Les montants sont des chaînes dans les contrats, jamais des nombres.
/// </summary>
public static class EventJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
