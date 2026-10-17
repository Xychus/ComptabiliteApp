using System.Globalization;
using Ledger.Command.Domain.ConstrainedAccounts;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Periods;
using Ledger.Contracts.Events.V1;
using Ledger.SharedKernel;

namespace Ledger.Command.Application.Events;

/// <summary>
/// Conversion des événements du domaine vers leurs contrats V1 (forme stockée et publiée).
/// Une surcharge par type d'événement : ajouter un événement = ajouter une surcharge.
/// </summary>
public static class EventContractMapper
{
    public static JournalEntryPostedV1 ToContract(JournalEntryPosted posted)
    {
        ArgumentNullException.ThrowIfNull(posted);
        return new JournalEntryPostedV1(
            posted.EntryId.Value,
            posted.Entity.Value,
            posted.BusinessDate,
            posted.Period.ToString(),
            posted.Description,
            [.. posted.Lines.Select(ToContract)]);
    }

    public static JournalEntryReversedV1 ToContract(JournalEntryReversed reversed)
    {
        ArgumentNullException.ThrowIfNull(reversed);
        return new JournalEntryReversedV1(
            reversed.ReversalEntryId.Value,
            reversed.ReversalOf.Value,
            reversed.Entity.Value,
            reversed.BusinessDate,
            reversed.Period.ToString(),
            reversed.Description,
            [.. reversed.Lines.Select(ToContract)]);
    }

    public static AccountLinePostedV1 ToContract(AccountLinePosted posted)
    {
        ArgumentNullException.ThrowIfNull(posted);
        return new AccountLinePostedV1(
            posted.Entity.Value,
            posted.Account.Value,
            posted.SourceEntryId.Value,
            posted.Side.ToString(),
            ToContract(posted.Amount),
            ToContract(posted.BalanceAfter));
    }

    public static PeriodOpenedV1 ToContract(PeriodOpened opened)
    {
        ArgumentNullException.ThrowIfNull(opened);
        return new PeriodOpenedV1(opened.Entity.Value, opened.Period.ToString());
    }

    public static PeriodSealedV1 ToContract(PeriodSealed sealedPeriod)
    {
        ArgumentNullException.ThrowIfNull(sealedPeriod);
        return new PeriodSealedV1(sealedPeriod.Entity.Value, sealedPeriod.Period.ToString());
    }

    private static JournalLineV1 ToContract(JournalLine line) =>
        new(line.Account.Value, line.Side.ToString(), ToContract(line.Amount));

    /// <summary>Chaîne décimale à l'échelle de la devise : <c>5</c> devient <c>"5.00"</c>.</summary>
    private static MoneyV1 ToContract(Money money) =>
        new(money.Amount.ToString("F" + money.Currency.MinorUnits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture), money.Currency.Code);
}
