using Ledger.Command.Domain.Accounts;
using Ledger.SharedKernel;

namespace Ledger.Command.Domain.Entries.Rules;

/// <summary>
/// Règle de validité d'une écriture, évaluée sur le contenu de la commande seul (et le plan
/// comptable de référence). Ajouter une règle = ajouter une classe, sans modifier les autres.
/// </summary>
public interface IEntryRule
{
    /// <summary>Renvoie l'erreur de la première ligne fautive, ou <c>null</c> si la règle est respectée.</summary>
    DomainError? Check(IReadOnlyList<JournalLine> lines, IChartOfAccounts chart);
}

/// <summary>Ensemble ordonné de règles ; la première violation l'emporte.</summary>
public sealed class EntryRuleSet(IEnumerable<IEntryRule> rules)
{
    private readonly IEntryRule[] _rules = [.. rules];

    /// <summary>Règles par défaut, de la plus structurelle à la plus métier.</summary>
    public static EntryRuleSet Default { get; } = new(
    [
        new AtLeastTwoLinesRule(),
        new StrictlyPositiveAmountsRule(),
        new MinorUnitPrecisionRule(),
        new SingleCurrencyRule(),
        new ActiveAccountRule(),
        new AccountCurrencyRule(),
        new BalancedPerCurrencyRule(),
    ]);

    public DomainError? Check(IReadOnlyList<JournalLine> lines, IChartOfAccounts chart)
    {
        foreach (var rule in _rules)
        {
            if (rule.Check(lines, chart) is { } error)
            {
                return error;
            }
        }

        return null;
    }
}
