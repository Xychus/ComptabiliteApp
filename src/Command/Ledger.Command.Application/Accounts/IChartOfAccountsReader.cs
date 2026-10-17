using Ledger.Command.Application.EventStore;
using Ledger.Command.Domain.Accounts;
using Ledger.SharedKernel;

namespace Ledger.Command.Application.Accounts;

/// <summary>Charge le plan comptable d'une entité, tel que les décisions d'écriture en ont besoin.</summary>
public interface IChartOfAccountsReader
{
    Task<IChartOfAccounts> LoadAsync(EntityId entity, CancellationToken cancellationToken);
}

/// <summary>Plan comptable reconstruit depuis le flux <c>chart-{entité}</c> du journal (ADR 0012), jamais d'une projection.</summary>
public sealed class JournalChartOfAccountsReader(AggregateStreamReader streams) : IChartOfAccountsReader
{
    public async Task<IChartOfAccounts> LoadAsync(EntityId entity, CancellationToken cancellationToken)
    {
        var chart = await streams.LoadAsync(StreamIds.Chart(entity), ChartState.Initial(entity), (s, e) => s.Evolve(e), cancellationToken).ConfigureAwait(false);
        return chart.State;
    }
}
