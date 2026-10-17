using System.Globalization;
using Ledger.Command.Domain.ConstrainedAccounts;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Periods;
using Ledger.Contracts.Events.V1;
using Ledger.SharedKernel;

namespace Ledger.Command.Application.Events;

/// <summary>
/// Conversion des événements du domaine vers leurs contrats V1 (forme stockée et publiée), et retour.
/// Une paire de surcharges par type d'événement : ajouter un événement = ajouter une paire.
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

    public static JournalEntryPosted ToDomain(JournalEntryPostedV1 contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return new JournalEntryPosted(
            new EntryId(contract.EntryId),
            new EntityId(contract.EntityId),
            contract.BusinessDate,
            Stored(PeriodId.Parse(contract.Period)),
            contract.Description,
            EquatableList.Of(contract.Lines.Select(ToDomain)));
    }

    public static JournalEntryReversed ToDomain(JournalEntryReversedV1 contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return new JournalEntryReversed(
            new EntryId(contract.ReversalEntryId),
            new EntryId(contract.ReversalOf),
            new EntityId(contract.EntityId),
            contract.BusinessDate,
            Stored(PeriodId.Parse(contract.Period)),
            contract.Description,
            EquatableList.Of(contract.Lines.Select(ToDomain)));
    }

    public static AccountLinePosted ToDomain(AccountLinePostedV1 contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return new AccountLinePosted(
            new EntityId(contract.EntityId),
            new AccountId(contract.AccountId),
            new EntryId(contract.SourceEntryId),
            ToSide(contract.Side),
            ToDomain(contract.Amount),
            ToDomain(contract.BalanceAfter));
    }

    public static PeriodOpened ToDomain(PeriodOpenedV1 contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return new PeriodOpened(new EntityId(contract.EntityId), Stored(PeriodId.Parse(contract.Period)));
    }

    public static PeriodSealed ToDomain(PeriodSealedV1 contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        return new PeriodSealed(new EntityId(contract.EntityId), Stored(PeriodId.Parse(contract.Period)));
    }

    private static JournalLineV1 ToContract(JournalLine line) =>
        new(line.Account.Value, line.Side.ToString(), ToContract(line.Amount));

    /// <summary>Chaîne décimale à l'échelle de la devise : <c>5</c> devient <c>"5.00"</c>.</summary>
    private static MoneyV1 ToContract(Money money) =>
        new(money.Amount.ToString("F" + money.Currency.MinorUnits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture), money.Currency.Code);

    private static JournalLine ToDomain(JournalLineV1 line) =>
        new(new AccountId(line.AccountId), ToSide(line.Side), ToDomain(line.Amount));

    private static Money ToDomain(MoneyV1 money) =>
        new(decimal.Parse(money.Amount, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture), Stored(Currency.FromCode(money.Currency)));

    private static Side ToSide(string side) => side switch
    {
        nameof(Side.Debit) => Side.Debit,
        nameof(Side.Credit) => Side.Credit,
        _ => throw new InvalidOperationException($"Sens inconnu dans un événement stocké : « {side} »."),
    };

    /// <summary>Un événement stocké invalide est une corruption du journal, pas un rejet métier.</summary>
    private static T Stored<T>(Result<T> result) =>
        result.IsSuccess ? result.Value : throw new InvalidOperationException($"Événement stocké invalide : {result.Error.Message}");
}
