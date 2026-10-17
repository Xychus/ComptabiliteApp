using Ledger.Command.Api.Observability;
using Ledger.Command.Application.Accounts;
using Ledger.Command.Application.Entries;
using Ledger.Command.Application.Events;
using Ledger.Command.Application.EventStore;
using Ledger.Command.Application.Periods;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Infrastructure;
using Ledger.Command.Infrastructure.Postgres;
using Ledger.SharedKernel;

namespace Ledger.Command.Api.Composition;

/// <summary>Gestionnaires de commandes de l'Application et implémentation de l'event store.</summary>
public static class CommandServices
{
    /// <summary>Clé de configuration du fournisseur d'event store : <c>Postgres</c> (défaut) ou <c>InMemory</c>.</summary>
    public const string ProviderKey = "Ledger:EventStore:Provider";

    /// <summary>Chaîne de connexion du rôle <c>ledger_app</c>, injectée depuis Secrets Manager.</summary>
    public const string ConnectionStringName = "Ledger";

    public const string InMemoryProvider = "InMemory";

    public static IServiceCollection AddLedgerCommands(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton(EventCatalog.Default);
        services.AddSingleton(JournalEntryDecider.Default);
        services.AddSingleton(ConcurrencyRetryPolicy.Default);
        services.AddSingleton<AggregateStreamReader>();
        services.AddSingleton<IChartOfAccountsReader, JournalChartOfAccountsReader>();
        services.AddScoped<PostJournalEntryHandler>();
        services.AddScoped<ReverseJournalEntryHandler>();
        services.AddScoped<PeriodJournal>();
        services.AddScoped<OpenPeriodHandler>();
        services.AddScoped<SealPeriodHandler>();
        services.AddScoped<DefineAccountHandler>();

        var healthChecks = services.AddHealthChecks();
        if (string.Equals(configuration[ProviderKey], InMemoryProvider, StringComparison.Ordinal))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException($"L'event store en mémoire ({ProviderKey}={InMemoryProvider}) est réservé au développement.");
            }

            services.AddInMemoryEventStore();
            return services;
        }

        // Lue à la résolution, pas à l'enregistrement : la génération du contrat OpenAPI au build
        // compose l'application sans chaîne de connexion. Son absence fait échouer le démarrage.
        services.AddPostgresEventStore(sp => sp.GetRequiredService<IConfiguration>().GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Chaîne de connexion « {ConnectionStringName} » absente."));
        if (!DocumentGeneration.IsRunning)
        {
            services.AddHostedService<ConnectionStringStartupCheck>();
        }

        healthChecks.AddCheck<PostgresHealthCheck>("postgres", tags: [LedgerHealthChecks.ReadyTag]);
        return services;
    }

    /// <summary>Fait échouer le démarrage, plutôt que la première requête, si la chaîne de connexion manque.</summary>
    private sealed class ConnectionStringStartupCheck(IConfiguration configuration) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) =>
            string.IsNullOrWhiteSpace(configuration.GetConnectionString(ConnectionStringName))
                ? throw new InvalidOperationException($"Chaîne de connexion « {ConnectionStringName} » absente.")
                : Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
