using Ledger.Projections.Application.Balances;
using Ledger.Projections.Application.Chart;
using Ledger.Projections.Application.Entries;
using Ledger.Projections.Application.Events;
using Ledger.Projections.Application.Periods;
using Ledger.Projections.Application.Processing;
using Ledger.Projections.Application.Rebuild;
using Ledger.Projections.Application.Snapshots;
using Ledger.Projections.Application.Taxes;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Projections.Application;

/// <summary>
/// Projections et leur moteur. Les projections et les ports de stockage sont par portée (une
/// portée = une transaction) ; les implémentations des ports viennent de l'infrastructure.
/// </summary>
public static class ProjectionServiceCollectionExtensions
{
    public static IServiceCollection AddLedgerProjections(this IServiceCollection services, TaxAccountMap taxAccounts)
    {
        ArgumentNullException.ThrowIfNull(taxAccounts);
        services.AddSingleton(EventDecoder.Default);
        services.AddSingleton(taxAccounts);
        services.AddScoped<IProjection, AccountBalancesProjection>();
        services.AddScoped<IProjection, LedgerEntriesProjection>();
        services.AddScoped<IProjection, ChartOfAccountsProjection>();
        services.AddScoped<IProjection, PeriodsProjection>();
        services.AddScoped<IProjection, PeriodSnapshotsProjection>();
        services.AddScoped<IProjection, TaxProjection>();
        services.AddScoped<ProjectionApplier>();
        services.AddScoped<ProjectionMessageProcessor>();
        services.AddSingleton<ProjectionPump>();
        services.AddSingleton<ProjectionRebuilder>();
        return services;
    }
}
