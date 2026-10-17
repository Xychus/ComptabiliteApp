using Ledger.Command.Application.EventStore;
using Ledger.Command.Domain.Accounts;
using Ledger.Command.Domain.ConstrainedAccounts;
using Ledger.Command.Domain.Entries;
using Ledger.SharedKernel;

namespace Ledger.Command.Application.Entries;

/// <summary>
/// Applique chaque ligne d'un compte contraint à la version suivante du flux du compte, avec
/// <c>balance_after</c> (ADR 0004). Partagé par la passation et la contre-passation.
/// </summary>
internal sealed class ConstrainedAccountLines(AggregateStreamReader streams)
{
    /// <returns>Le premier rejet (solde insuffisant), ou <c>null</c> si toutes les lignes sont acceptées.</returns>
    public async Task<DomainError?> AddAsync(
        EntityId entity,
        EntryId sourceEntryId,
        IEnumerable<JournalLine> lines,
        IChartOfAccounts chart,
        PendingBatch batch,
        CancellationToken cancellationToken)
    {
        var accounts = new Dictionary<AccountId, (ConstrainedAccountState State, long Version)>();
        foreach (var line in lines)
        {
            if (chart.Find(line.Account) is not { NoNegativeBalance: true } account)
            {
                continue;
            }

            var streamId = StreamIds.Account(entity, account.Id);
            if (!accounts.TryGetValue(account.Id, out var current))
            {
                var loaded = await streams.LoadAsync(streamId, ConstrainedAccountState.Initial(entity, account), (s, e) => s.Evolve(e), cancellationToken).ConfigureAwait(false);
                current = (loaded.State, loaded.Version);
            }

            var decision = ConstrainedAccountDecider.Decide(current.State, new ApplyAccountLine(sourceEntryId, line));
            if (decision.IsFailure)
            {
                return decision.Error;
            }

            foreach (var domainEvent in decision.Value)
            {
                batch.Add(streamId, ++current.Version, domainEvent, balanceAfter: ((AccountLinePosted)domainEvent).BalanceAfter.Amount);
                current.State = current.State.Evolve(domainEvent);
            }

            accounts[account.Id] = current;
        }

        return null;
    }
}
