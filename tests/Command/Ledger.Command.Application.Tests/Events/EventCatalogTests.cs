using System.Text.Json;
using Ledger.Command.Application.Events;
using Ledger.Command.Domain;
using Ledger.Command.Domain.ConstrainedAccounts;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Periods;
using Ledger.Contracts.Events.V1;
using Ledger.SharedKernel;

namespace Ledger.Command.Application.Tests.Events;

public sealed class EventCatalogTests
{
    private static readonly EntityId _acme = new("ACME");
    private static readonly PeriodId _march = new(2026, 3);
    private static readonly EquatableList<JournalLine> _lines = EquatableList.Of(
        new JournalLine(new AccountId("1000"), Side.Debit, new Money(114.98m, Currency.Cad)),
        new JournalLine(new AccountId("4000"), Side.Credit, new Money(100m, Currency.Cad)),
        new JournalLine(new AccountId("2300"), Side.Credit, new Money(5m, Currency.Cad)),
        new JournalLine(new AccountId("2310"), Side.Credit, new Money(9.98m, Currency.Cad)));

    public static TheoryData<IDomainEvent, string> Evenements => new()
    {
        { new JournalEntryPosted(EntryId.New(), _acme, new DateOnly(2026, 3, 15), _march, "Vente « comptoir »", _lines), EventTypes.JournalEntryPosted },
        { new JournalEntryReversed(EntryId.New(), EntryId.New(), _acme, new DateOnly(2026, 3, 20), _march, null, _lines), EventTypes.JournalEntryReversed },
        { new AccountLinePosted(_acme, new AccountId("1000"), EntryId.New(), Side.Credit, new Money(60m, Currency.Cad), new Money(40m, Currency.Cad)), EventTypes.AccountLinePosted },
        { new PeriodOpened(_acme, _march), EventTypes.PeriodOpened },
        { new PeriodSealed(_acme, _march), EventTypes.PeriodSealed },
    };

    [Theory]
    [MemberData(nameof(Evenements))]
    public void Aller_retour_domaine_JSON_domaine(IDomainEvent domainEvent, string expectedType)
    {
        var serialized = EventCatalog.Default.Serialize(domainEvent);

        Assert.Equal(expectedType, serialized.EventType);
        Assert.Equal(EventSchema.Version, serialized.SchemaVersion);
        Assert.Equal(domainEvent, EventCatalog.Default.Deserialize(serialized.EventType, serialized.SchemaVersion, serialized.Payload));
    }

    [Fact]
    public void La_charge_est_en_camelCase_avec_des_montants_en_chaine()
    {
        var payload = EventCatalog.Default.Serialize(new PeriodOpened(_acme, _march)).Payload;
        var line = EventCatalog.Default.Serialize(new JournalEntryPosted(EntryId.New(), _acme, new DateOnly(2026, 3, 15), _march, null, _lines)).Payload.GetProperty("lines")[3];

        Assert.Equal("2026-03", payload.GetProperty("period").GetString());
        Assert.Equal("9.98", line.GetProperty("amount").GetProperty("amount").GetString());
        Assert.Equal("Credit", line.GetProperty("side").GetString());
    }

    [Fact]
    public void Un_evenement_sans_contrat_est_un_bogue() =>
        Assert.Throws<InvalidOperationException>(() => EventCatalog.Default.Serialize(new UnknownEvent()));

    [Fact]
    public void Un_type_stocke_inconnu_est_une_corruption() =>
        Assert.Throws<InvalidOperationException>(() => EventCatalog.Default.Deserialize("Inconnu", 1, Json("{}")));

    [Fact]
    public void Une_version_de_schema_future_n_est_pas_lue_sans_upcasting() =>
        Assert.Throws<NotSupportedException>(() => EventCatalog.Default.Deserialize(EventTypes.PeriodOpened, 2, Json("""{"entityId":"ACME","period":"2026-03"}""")));

    [Theory]
    [InlineData("""{"entityId":"ACME","period":"2026-13"}""")]
    [InlineData("null")]
    public void Une_charge_stockee_invalide_est_une_corruption(string payload) =>
        Assert.Throws<InvalidOperationException>(() => EventCatalog.Default.Deserialize(EventTypes.PeriodOpened, 1, Json(payload)));

    [Theory]
    [InlineData("XXX", "Debit")]
    [InlineData("CAD", "Gauche")]
    public void Une_ligne_stockee_invalide_est_une_corruption(string currency, string side)
    {
        var payload = Json($$"""
            {"entityId":"ACME","accountId":"1000","sourceEntryId":"{{Guid.CreateVersion7()}}","side":"{{side}}",
             "amount":{"amount":"1.00","currency":"{{currency}}"},"balanceAfter":{"amount":"1.00","currency":"{{currency}}"} }
            """);

        Assert.Throws<InvalidOperationException>(() => EventCatalog.Default.Deserialize(EventTypes.AccountLinePosted, 1, payload));
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed record UnknownEvent : IDomainEvent;
}
