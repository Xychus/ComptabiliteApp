using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ledger.Contracts.Events;
using Ledger.Contracts.Events.V1;

namespace Ledger.Projections.Tests.Support;

/// <summary>
/// Fabrique d'enveloppes cohérentes pour une entité, comme les publierait l'outbox : flux,
/// versions, séquences croissantes, nombre d'événements du sceau. Les séquences sont
/// remplacées par celles de PostgreSQL quand les événements y sont insérés.
/// </summary>
public sealed class JournalBuilder(string entityId)
{
    public const string Currency = "CAD";
    public const string Actor = "client-tests";

    public static readonly DateTimeOffset BaseTime = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<string, long> _streamVersions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _periodCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, JournalLineV1[]> _entries = [];
    private long _sequence;

    public string EntityId { get; } = entityId;

    /// <summary>Prochaine séquence attribuée (modifiable pour simuler une validation hors ordre).</summary>
    public long NextSequence
    {
        get => _sequence + 1;
        set => _sequence = value - 1;
    }

    public static JournalLineV1 Debit(string account, decimal amount, string currency = Currency) => Line(account, "Debit", amount, currency);

    public static JournalLineV1 Credit(string account, decimal amount, string currency = Currency) => Line(account, "Credit", amount, currency);

    public static string PeriodOf(DateOnly date) => date.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    public EventEnvelopeV1 Posted(DateOnly businessDate, params JournalLineV1[] lines) => Posted(Guid.CreateVersion7(), businessDate, lines);

    public EventEnvelopeV1 Posted(Guid entryId, DateOnly businessDate, params JournalLineV1[] lines)
    {
        _entries[entryId] = lines;
        var period = PeriodOf(businessDate);
        return Envelope(
            $"entry-{entryId}",
            EventTypes.JournalEntryPosted,
            businessDate,
            period,
            new JournalEntryPostedV1(entryId, EntityId, businessDate, period, $"Écriture {entryId}", lines));
    }

    /// <summary>Contre-passation : lignes de l'écriture d'origine, sens inversés, dans le flux d'origine (version 2).</summary>
    public EventEnvelopeV1 Reversed(Guid entryId, DateOnly businessDate, Guid? reversalEntryId = null)
    {
        var lines = _entries[entryId].Select(line => line with { Side = line.Side == "Debit" ? "Credit" : "Debit" }).ToArray();
        var period = PeriodOf(businessDate);
        return Envelope(
            $"entry-{entryId}",
            EventTypes.JournalEntryReversed,
            businessDate,
            period,
            new JournalEntryReversedV1(reversalEntryId ?? Guid.CreateVersion7(), entryId, EntityId, businessDate, period, "Contre-passation", lines));
    }

    /// <summary>Ligne d'un compte contraint (ADR 0004) : elle répète une ligne d'écriture.</summary>
    public EventEnvelopeV1 AccountLine(Guid sourceEntryId, DateOnly businessDate, JournalLineV1 line, decimal balanceAfter) =>
        Envelope(
            $"account-{EntityId}-{line.AccountId}",
            EventTypes.AccountLinePosted,
            businessDate,
            PeriodOf(businessDate),
            new AccountLinePostedV1(EntityId, line.AccountId, sourceEntryId, line.Side, line.Amount, new MoneyV1(Amount(balanceAfter), line.Amount.Currency)),
            balanceAfter);

    public EventEnvelopeV1 Opened(string period) =>
        Envelope($"period-{EntityId}-{period}", EventTypes.PeriodOpened, null, period, new PeriodOpenedV1(EntityId, period));

    /// <summary>Sceau : <c>eventCount</c> = événements fabriqués jusqu'ici pour l'entité dans la période.</summary>
    public EventEnvelopeV1 Sealed(string period, string? merkleRoot = null)
    {
        var count = _periodCounts.GetValueOrDefault(period);
        return Envelope(
            $"period-{EntityId}-{period}",
            EventTypes.PeriodSealed,
            null,
            period,
            new PeriodSealedV1(EntityId, period) { EventCount = count, MerkleRoot = merkleRoot ?? Hex($"merkle-{EntityId}-{period}") });
    }

    public EventEnvelopeV1 AccountDefined(string accountId, string name, string normalSide = "Debit", bool isActive = true, bool noNegativeBalance = false, string period = "2026-03") =>
        Envelope(
            $"chart-{EntityId}",
            EventTypes.AccountDefined,
            null,
            period,
            new AccountDefinedV1(EntityId, accountId, name, Currency, normalSide, isActive, noNegativeBalance));

    private static JournalLineV1 Line(string account, string side, decimal amount, string currency) =>
        new(account, side, new MoneyV1(Amount(amount), currency));

    private static string Amount(decimal amount) => amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Hex(string seed) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(seed)));

    private EventEnvelopeV1 Envelope<T>(string streamId, string eventType, DateOnly? businessDate, string period, T payload, decimal? balanceAfter = null)
    {
        var version = _streamVersions.GetValueOrDefault(streamId) + 1;
        _streamVersions[streamId] = version;
        _periodCounts[period] = _periodCounts.GetValueOrDefault(period) + 1;
        var sequence = ++_sequence;
        var eventId = Guid.CreateVersion7();
        return new EventEnvelopeV1(
            sequence,
            eventId,
            streamId,
            version,
            eventType,
            EventSchema.Version,
            EntityId,
            businessDate,
            BaseTime.AddSeconds(sequence),
            period,
            balanceAfter is null ? null : Amount(balanceAfter.Value),
            Guid.CreateVersion7(),
            null,
            Actor,
            Hex(eventId.ToString()),
            version == 1 ? null : Hex($"{streamId}/{version - 1}"),
            JsonSerializer.SerializeToElement(payload, EventJson.Options));
    }
}
