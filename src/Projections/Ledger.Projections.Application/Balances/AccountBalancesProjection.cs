using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Entries;
using Ledger.Projections.Application.Events;
using Ledger.Projections.Application.Processing;

namespace Ledger.Projections.Application.Balances;

/// <summary>Port d'écriture de <c>account_balances</c> : cumul des débits et crédits par compte et devise.</summary>
public interface IAccountBalanceStore
{
    /// <summary>Ajoute le mouvement aux totaux du compte (créés à zéro s'ils n'existent pas).</summary>
    Task AddAsync(string entityId, AccountMovement movement, CancellationToken cancellationToken);
}

/// <summary>
/// Soldes courants des comptes : chaque écriture et chaque contre-passation ajoute ses lignes.
/// Les <c>AccountLinePosted</c> des comptes contraints ne sont pas comptés : ils répètent les
/// lignes de l'écriture (ADR 0004).
/// </summary>
public sealed class AccountBalancesProjection(IAccountBalanceStore store)
    : IProjection, IEventHandler<JournalEntryPostedV1>, IEventHandler<JournalEntryReversedV1>
{
    public string Name => ProjectionNames.AccountBalances;

    public Task HandleAsync(ProjectedEvent<JournalEntryPostedV1> projectedEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectedEvent);
        return AddAsync(projectedEvent.Data.EntityId, projectedEvent.Data.Lines, cancellationToken);
    }

    public Task HandleAsync(ProjectedEvent<JournalEntryReversedV1> projectedEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectedEvent);
        return AddAsync(projectedEvent.Data.EntityId, projectedEvent.Data.Lines, cancellationToken);
    }

    private async Task AddAsync(string entityId, IReadOnlyList<JournalLineV1> lines, CancellationToken cancellationToken)
    {
        foreach (var movement in JournalLines.Movements(lines))
        {
            await store.AddAsync(entityId, movement, cancellationToken).ConfigureAwait(false);
        }
    }
}
