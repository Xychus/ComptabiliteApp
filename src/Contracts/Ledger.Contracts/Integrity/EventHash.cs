using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ledger.Contracts.Integrity;

/// <summary>
/// Champs d'un événement couverts par son hash : tous sauf <c>global_sequence</c> et le hash
/// lui-même (ADR 0005 et 0006).
/// </summary>
public sealed record EventHashInput(
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
    Guid CorrelationId,
    Guid? CausationId,
    string Actor,
    string? PreviousHash);

/// <summary>Hash d'un événement : SHA-256 de la forme canonique JCS de ses champs (ADR 0006).</summary>
public static class EventHash
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    /// <summary>Hash hexadécimal minuscule (64 caractères).</summary>
    public static string Compute(EventHashInput input) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalForm(input))));

    /// <summary>Forme canonique JCS hachée, exposée pour l'audit et les tests.</summary>
    public static string CanonicalForm(EventHashInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.RecordedAt.UtcTicks % TicksPerMicrosecond != 0)
        {
            throw new ArgumentException("recordedAt doit être tronqué à la microseconde avant le calcul du hash.", nameof(input));
        }

        var fields = new JsonObject
        {
            ["eventId"] = Uuid(input.EventId),
            ["streamId"] = input.StreamId,
            ["streamVersion"] = input.StreamVersion,
            ["eventType"] = input.EventType,
            ["schemaVersion"] = input.SchemaVersion,
            ["entityId"] = input.EntityId,
            ["businessDate"] = input.BusinessDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["recordedAt"] = input.RecordedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture),
            ["period"] = input.Period,
            ["payload"] = JsonNode.Parse(input.Payload.GetRawText()),
            ["balanceAfter"] = input.BalanceAfter is { } balance ? NormalizedDecimal(balance) : null,
            ["correlationId"] = Uuid(input.CorrelationId),
            ["causationId"] = input.CausationId is { } causation ? Uuid(causation) : null,
            ["actor"] = input.Actor,
            ["previousHash"] = input.PreviousHash,
        };

        return JsonCanonicalizer.Canonicalize(JsonSerializer.SerializeToElement(fields));
    }

    /// <summary>Tronque à la microseconde, précision de <c>TIMESTAMPTZ</c> en PostgreSQL.</summary>
    public static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.UtcTicks - (value.UtcTicks % TicksPerMicrosecond), TimeSpan.Zero);

    /// <summary>Chaîne décimale sans zéros de fin ni exposant : <c>40.00</c> et <c>40</c> donnent <c>"40"</c>.</summary>
    public static string NormalizedDecimal(decimal value) =>
        value == 0m ? "0" : value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static string Uuid(Guid value) => value.ToString("D", CultureInfo.InvariantCulture);
}
