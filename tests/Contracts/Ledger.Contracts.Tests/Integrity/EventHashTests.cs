using System.Text.Json;
using Ledger.Contracts.Integrity;

namespace Ledger.Contracts.Tests.Integrity;

public sealed class EventHashTests
{
    private static readonly EventHashInput _reference = new(
        EventId: Guid.Parse("01920000-0000-7000-8000-000000000001"),
        StreamId: "account-ACME-1000",
        StreamVersion: 2,
        EventType: "AccountLinePosted",
        SchemaVersion: 1,
        EntityId: "ACME",
        BusinessDate: new DateOnly(2026, 3, 15),
        RecordedAt: new DateTimeOffset(2026, 3, 15, 14, 30, 0, TimeSpan.Zero).AddTicks(1_234_560),
        Period: "2026-03",
        Payload: Json("""{"side":"Credit","amount":{"currency":"CAD","amount":"60.00"}}"""),
        BalanceAfter: 40.00m,
        CorrelationId: Guid.Parse("01920000-0000-7000-8000-0000000000aa"),
        CausationId: null,
        Actor: "pos-terminal",
        PreviousHash: new string('a', 64));

    /// <summary>
    /// Forme canonique de référence : la modifier invalide tous les hash déjà stockés (ADR 0006).
    /// Le hash attendu a été recalculé indépendamment (<c>printf '%s' … | sha256sum</c>).
    /// </summary>
    [Fact]
    public void Forme_canonique_et_hash_de_reference()
    {
        Assert.Equal(
            """{"actor":"pos-terminal","balanceAfter":"40","businessDate":"2026-03-15","causationId":null,"correlationId":"01920000-0000-7000-8000-0000000000aa","entityId":"ACME","eventId":"01920000-0000-7000-8000-000000000001","eventType":"AccountLinePosted","payload":{"amount":{"amount":"60.00","currency":"CAD"},"side":"Credit"},"period":"2026-03","previousHash":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","recordedAt":"2026-03-15T14:30:00.123456Z","schemaVersion":1,"streamId":"account-ACME-1000","streamVersion":2}""",
            EventHash.CanonicalForm(_reference));
        Assert.Equal(ReferenceHash, EventHash.Compute(_reference));
    }

    [Fact]
    public void Indifferent_a_la_representation_du_payload_et_a_l_echelle_du_solde()
    {
        var reformatted = _reference with
        {
            Payload = Json("""
                { "amount": { "amount": "60.00", "currency": "CAD" },
                  "side": "Credit" }
                """),
            BalanceAfter = 40m,
            RecordedAt = _reference.RecordedAt.ToOffset(TimeSpan.FromHours(-4)),
        };

        Assert.Equal(EventHash.Compute(_reference), EventHash.Compute(reformatted));
    }

    public static TheoryData<EventHashInput> Variantes => new()
    {
        _reference with { EventId = Guid.Parse("01920000-0000-7000-8000-000000000002") },
        _reference with { StreamId = "account-ACME-1001" },
        _reference with { StreamVersion = 3 },
        _reference with { EventType = "JournalEntryPosted" },
        _reference with { SchemaVersion = 2 },
        _reference with { EntityId = "ACME2" },
        _reference with { BusinessDate = new DateOnly(2026, 3, 16) },
        _reference with { BusinessDate = null },
        _reference with { RecordedAt = _reference.RecordedAt.AddTicks(10) },
        _reference with { Period = "2026-04" },
        _reference with { Payload = Json("""{"side":"Credit","amount":{"currency":"CAD","amount":"60.01"}}""") },
        _reference with { BalanceAfter = 40.01m },
        _reference with { BalanceAfter = null },
        _reference with { CorrelationId = Guid.Parse("01920000-0000-7000-8000-0000000000ab") },
        _reference with { CausationId = Guid.Parse("01920000-0000-7000-8000-0000000000ac") },
        _reference with { Actor = "autre" },
        _reference with { PreviousHash = null },
    };

    [Theory]
    [MemberData(nameof(Variantes))]
    public void Chaque_champ_est_couvert_par_le_hash(EventHashInput variant) =>
        Assert.NotEqual(EventHash.Compute(_reference), EventHash.Compute(variant));

    [Fact]
    public void Horodatage_plus_precis_que_la_microseconde_refuse() =>
        Assert.Throws<ArgumentException>(() => EventHash.Compute(_reference with { RecordedAt = _reference.RecordedAt.AddTicks(1) }));

    [Fact]
    public void Troncature_a_la_microseconde()
    {
        var value = new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.FromHours(-4)).AddTicks(1_234_567);

        var truncated = EventHash.TruncateToMicroseconds(value);

        Assert.Equal(value.UtcTicks - 7, truncated.UtcTicks);
        Assert.Equal(TimeSpan.Zero, truncated.Offset);
    }

    [Theory]
    [InlineData("40.00", "40")]
    [InlineData("40.50", "40.5")]
    [InlineData("-0.10", "-0.1")]
    [InlineData("0.00", "0")]
    [InlineData("123456789012345678.99", "123456789012345678.99")]
    public void Decimal_normalise(string value, string expected) =>
        Assert.Equal(expected, EventHash.NormalizedDecimal(decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture)));

    private const string ReferenceHash = "75578937fc4f6261e0615349df31961c1253c6fe8e0a43aae2c36f1225fd3941";

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
