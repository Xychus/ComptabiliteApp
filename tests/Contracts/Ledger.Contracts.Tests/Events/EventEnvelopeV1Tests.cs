using System.Text.Json;
using Ledger.Contracts.Events;
using Ledger.Contracts.Events.V1;

namespace Ledger.Contracts.Tests.Events;

public sealed class EventEnvelopeV1Tests
{
    [Fact]
    public void Enveloppe_en_camelCase_avec_montant_en_chaine_et_aller_retour()
    {
        using var payload = JsonDocument.Parse("""{"entityId":"ACME","period":"2026-03"}""");
        var envelope = new EventEnvelopeV1(
            42, Guid.CreateVersion7(), "period-ACME-2026%2D03", 1, EventTypes.PeriodOpened, 1, "ACME", null,
            new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), "2026-03", "40.00", Guid.CreateVersion7(), null,
            "admin", new string('b', 64), null, payload.RootElement.Clone());

        var json = JsonSerializer.Serialize(envelope, EventJson.Options);
        var back = JsonSerializer.Deserialize<EventEnvelopeV1>(json, EventJson.Options)!;

        Assert.Contains("\"globalSequence\":42", json, StringComparison.Ordinal);
        Assert.Contains("\"balanceAfter\":\"40.00\"", json, StringComparison.Ordinal);
        Assert.Equal(envelope with { Payload = default }, back with { Payload = default });
        Assert.Equal("ACME", back.Payload.GetProperty("entityId").GetString());
    }

    [Fact]
    public void Options_en_lecture_seule() =>
        Assert.True(EventJson.Options.IsReadOnly);
}
