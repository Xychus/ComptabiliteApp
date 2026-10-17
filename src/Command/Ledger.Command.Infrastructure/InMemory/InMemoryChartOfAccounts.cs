using System.Collections.Concurrent;
using Ledger.Command.Application.Accounts;
using Ledger.Command.Domain.Accounts;
using Ledger.Command.Infrastructure.Accounts;
using Ledger.SharedKernel;

namespace Ledger.Command.Infrastructure.InMemory;

/// <summary>Plan comptable en mémoire, pour les tests et le développement.</summary>
public sealed class InMemoryChartOfAccounts : IChartOfAccountsReader
{
    private readonly ConcurrentDictionary<(EntityId Entity, AccountId Account), AccountInfo> _accounts = new();

    public void Define(EntityId entity, AccountInfo account)
    {
        ArgumentNullException.ThrowIfNull(account);
        _accounts[(entity, account.Id)] = account;
    }

    public Task<IChartOfAccounts> LoadAsync(EntityId entity, CancellationToken cancellationToken)
    {
        var accounts = _accounts.Where(pair => pair.Key.Entity == entity).ToDictionary(pair => pair.Key.Account, pair => pair.Value);
        return Task.FromResult<IChartOfAccounts>(new ChartSnapshot(accounts));
    }
}
