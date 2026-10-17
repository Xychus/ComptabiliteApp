using System.Text.Json;
using Ledger.Command.Application.Events;
using Ledger.Command.Domain.ConstrainedAccounts;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Periods;
using Ledger.Contracts.Events.V1;
using Ledger.SharedKernel;

namespace Ledger.Command.Application.Tests.Events;

public sealed class EventContractMapperTests
{
    private static readonly EntityId _acme = new("ACME");
    private static readonly PeriodId _march = new(2026, 3);
    private static readonly DateOnly _date = new(2026, 3, 15);
    private static readonly EntryId _entryId = EntryId.New();
    private static readonly EquatableList<JournalLine> _lines = EquatableList.Of(
        new JournalLine(new AccountId("1100"), Side.Debit, new Money(105m, Currency.Cad)),
        new JournalLine(new AccountId("4000"), Side.Credit, new Money(100m, Currency.Cad)),
        new JournalLine(new AccountId("2300"), Side.Credit, new Money(5m, Currency.Cad)));

    [Fact]
    public void Ecriture_passee_vers_V1()
    {
        var contract = EventContractMapper.ToContract(new JournalEntryPosted(_entryId, _acme, _date, _march, "Vente", _lines));

        Assert.Equal(_entryId.Value, contract.EntryId);
        Assert.Equal("ACME", contract.EntityId);
        Assert.Equal("2026-03", contract.Period);
        Assert.Equal("Vente", contract.Description);
        Assert.Equal(1, contract.SchemaVersion);
        Assert.Equal(new JournalLineV1("2300", "Credit", new MoneyV1("5.00", "CAD")), contract.Lines[2]);
    }

    [Fact]
    public void Contre_passation_vers_V1()
    {
        var reversalId = EntryId.New();

        var contract = EventContractMapper.ToContract(new JournalEntryReversed(reversalId, _entryId, _acme, _date, _march, null, _lines));

        Assert.Equal(reversalId.Value, contract.ReversalEntryId);
        Assert.Equal(_entryId.Value, contract.ReversalOf);
        Assert.Equal(3, contract.Lines.Count);
        Assert.Equal(1, contract.SchemaVersion);
    }

    [Fact]
    public void Ligne_de_compte_contraint_vers_V1()
    {
        var contract = EventContractMapper.ToContract(new AccountLinePosted(
            _acme, new AccountId("1000"), _entryId, Side.Credit, new Money(60m, Currency.Cad), new Money(40m, Currency.Cad)));

        Assert.Equal(new AccountLinePostedV1("ACME", "1000", _entryId.Value, "Credit", new MoneyV1("60.00", "CAD"), new MoneyV1("40.00", "CAD")), contract);
    }

    [Fact]
    public void Evenements_de_periode_vers_V1()
    {
        Assert.Equal(new PeriodOpenedV1("ACME", "2026-03"), EventContractMapper.ToContract(new PeriodOpened(_acme, _march)));
        Assert.Equal(new PeriodSealedV1("ACME", "2026-03"), EventContractMapper.ToContract(new PeriodSealed(_acme, _march)));
    }

    [Fact]
    public void Les_montants_sont_serialises_en_chaine_decimale_jamais_en_nombre()
    {
        var json = JsonSerializer.Serialize(EventContractMapper.ToContract(new JournalEntryPosted(_entryId, _acme, _date, _march, null, _lines)));

        Assert.Contains("\"Amount\":\"105.00\"", json, StringComparison.Ordinal);
        Assert.Contains("\"SchemaVersion\":1", json, StringComparison.Ordinal);
        Assert.Contains("\"BusinessDate\":\"2026-03-15\"", json, StringComparison.Ordinal);
    }
}
