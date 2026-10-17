using System.Globalization;
using Ledger.Command.Domain.Accounts;
using Ledger.SharedKernel;

namespace Ledger.Command.Domain.Entries.Rules;

/// <summary>Une écriture comporte au moins deux lignes.</summary>
public sealed class AtLeastTwoLinesRule : IEntryRule
{
    public DomainError? Check(IReadOnlyList<JournalLine> lines, IChartOfAccounts chart)
    {
        ArgumentNullException.ThrowIfNull(lines);
        return lines.Count >= 2
            ? null
            : DomainError.WithDetails(EntryErrors.TooFewLinesCode, "Une écriture comporte au moins deux lignes.", ("lineCount", Text(lines.Count)));
    }

    private static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Chaque montant est strictement positif ; le sens est porté par <see cref="Side"/>.</summary>
public sealed class StrictlyPositiveAmountsRule : IEntryRule
{
    public DomainError? Check(IReadOnlyList<JournalLine> lines, IChartOfAccounts chart) =>
        EntryErrors.FirstLine(lines, line => !line.Amount.IsPositive, EntryErrors.NonPositiveAmountCode, "Le montant doit être strictement positif.");
}

/// <summary>Aucun montant plus fin que l'unité mineure de sa devise (ADR 0008, H9).</summary>
public sealed class MinorUnitPrecisionRule : IEntryRule
{
    public DomainError? Check(IReadOnlyList<JournalLine> lines, IChartOfAccounts chart) =>
        EntryErrors.FirstLine(lines, line => !line.Amount.IsWholeMinorUnits, EntryErrors.InvalidPrecisionCode, "Le montant a plus de décimales que la devise n'en autorise.");
}

/// <summary>Toutes les lignes sont dans la même devise (ADR 0008, H6).</summary>
public sealed class SingleCurrencyRule : IEntryRule
{
    public DomainError? Check(IReadOnlyList<JournalLine> lines, IChartOfAccounts chart)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0)
        {
            return null;
        }

        var currency = lines[0].Amount.Currency;
        return EntryErrors.FirstLine(lines, line => line.Amount.Currency != currency, EntryErrors.MixedCurrenciesCode, $"Toutes les lignes doivent être en {currency}.");
    }
}

/// <summary>Chaque compte existe dans le plan comptable et est actif.</summary>
public sealed class ActiveAccountRule : IEntryRule
{
    public DomainError? Check(IReadOnlyList<JournalLine> lines, IChartOfAccounts chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        return EntryErrors.FirstLine(lines, line => chart.Find(line.Account) is not { IsActive: true }, EntryErrors.InactiveAccountCode, "Le compte est inconnu ou inactif.");
    }
}

/// <summary>La devise de chaque ligne est celle de son compte (ADR 0008, H6).</summary>
public sealed class AccountCurrencyRule : IEntryRule
{
    public DomainError? Check(IReadOnlyList<JournalLine> lines, IChartOfAccounts chart)
    {
        ArgumentNullException.ThrowIfNull(chart);
        return EntryErrors.FirstLine(lines, line => chart.Find(line.Account) is { } account && account.Currency != line.Amount.Currency, EntryErrors.AccountCurrencyMismatchCode, "La devise de la ligne diffère de celle du compte.");
    }
}

/// <summary>Partie double : somme des débits = somme des crédits, par devise.</summary>
public sealed class BalancedPerCurrencyRule : IEntryRule
{
    public DomainError? Check(IReadOnlyList<JournalLine> lines, IChartOfAccounts chart)
    {
        ArgumentNullException.ThrowIfNull(lines);
        foreach (var group in lines.GroupBy(line => line.Amount.Currency))
        {
            var debits = group.Where(l => l.Side == Side.Debit).Sum(l => l.Amount.Amount);
            var credits = group.Where(l => l.Side == Side.Credit).Sum(l => l.Amount.Amount);
            if (debits != credits)
            {
                return DomainError.WithDetails(
                    EntryErrors.NotBalancedCode,
                    $"Écriture déséquilibrée en {group.Key} : débits {Text(debits)}, crédits {Text(credits)}.",
                    ("currency", group.Key.Code),
                    ("debits", Text(debits)),
                    ("credits", Text(credits)));
            }
        }

        return null;
    }

    private static string Text(decimal value) => value.ToString(CultureInfo.InvariantCulture);
}
