using Ledger.Command.Domain.Entries;
using Ledger.SharedKernel;

namespace Ledger.Command.Domain.Accounts;

/// <summary>
/// Compte du plan comptable, tel que la décision en a besoin.
/// <paramref name="NormalSide"/> : sens dans lequel le solde est positif (débit pour un actif,
/// crédit pour un passif ou un produit) — ADR 0008, H7.
/// </summary>
public sealed record AccountInfo(
    AccountId Id,
    Currency Currency,
    Side NormalSide,
    bool IsActive,
    bool NoNegativeBalance);

/// <summary>
/// Port de lecture du plan comptable d'une entité (données de référence, pas une projection).
/// Implémenté par l'infrastructure à partir de la table <c>accounts</c>.
/// </summary>
public interface IChartOfAccounts
{
    AccountInfo? Find(AccountId account);
}
