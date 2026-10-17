using Ledger.Command.Domain.Accounts;
using Ledger.SharedKernel;

namespace Ledger.Command.Infrastructure.Accounts;

/// <summary>Plan comptable figé au moment de son chargement.</summary>
internal sealed class ChartSnapshot(IReadOnlyDictionary<AccountId, AccountInfo> accounts) : IChartOfAccounts
{
    public AccountInfo? Find(AccountId account) => accounts.GetValueOrDefault(account);
}
