using Ledger.Command.Domain.Accounts;
using Ledger.SharedKernel;

namespace Ledger.Command.Application.Accounts;

/// <summary>Charge le plan comptable d'une entité (table de référence <c>accounts</c>, pas une projection).</summary>
public interface IChartOfAccountsReader
{
    Task<IChartOfAccounts> LoadAsync(EntityId entity, CancellationToken cancellationToken);
}
