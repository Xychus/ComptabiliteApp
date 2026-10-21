using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Events;
using Ledger.Projections.Application.Processing;

namespace Ledger.Projections.Application.Chart;

/// <summary>État courant d'un compte ; <see cref="ChartVersion"/> est la version du flux <c>chart-{entité}</c> qui l'a défini.</summary>
public sealed record ChartAccount(
    string EntityId,
    string AccountId,
    string Name,
    string Currency,
    string NormalSide,
    bool IsActive,
    bool NoNegativeBalance,
    long ChartVersion,
    DateTimeOffset DefinedAt);

/// <summary>Port d'écriture de <c>chart_of_accounts</c>.</summary>
public interface IChartOfAccountsStore
{
    /// <summary>Remplace l'état du compte.</summary>
    Task SaveAsync(ChartAccount account, CancellationToken cancellationToken);
}

/// <summary>
/// Plan comptable lisible (libellés, sens normal), tiré des <c>AccountDefined</c> (ADR 0012).
/// Remplace la table <c>ledger.accounts</c>, supprimée par la migration 0007. Aucune décision
/// d'écriture ne la lit (invariant 4).
/// </summary>
public sealed class ChartOfAccountsProjection(IChartOfAccountsStore store) : IProjection, IEventHandler<AccountDefinedV1>
{
    public string Name => ProjectionNames.ChartOfAccounts;

    public Task HandleAsync(ProjectedEvent<AccountDefinedV1> projectedEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectedEvent);
        var data = projectedEvent.Data;
        return store.SaveAsync(
            new ChartAccount(
                data.EntityId,
                data.AccountId,
                data.Name,
                data.Currency,
                data.NormalSide,
                data.IsActive,
                data.NoNegativeBalance,
                projectedEvent.Context.StreamVersion,
                projectedEvent.Context.RecordedAt),
            cancellationToken);
    }
}
