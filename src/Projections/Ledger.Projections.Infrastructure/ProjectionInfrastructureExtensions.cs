using Ledger.Postgres.Infrastructure;
using Ledger.Projections.Application.Balances;
using Ledger.Projections.Application.Chart;
using Ledger.Projections.Application.Entries;
using Ledger.Projections.Application.Periods;
using Ledger.Projections.Application.Processing;
using Ledger.Projections.Application.Rebuild;
using Ledger.Projections.Application.Snapshots;
using Ledger.Projections.Application.Taxes;
using Ledger.Projections.Infrastructure.InMemory;
using Ledger.Projections.Infrastructure.Kafka;
using Ledger.Projections.Infrastructure.Postgres;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Projections.Infrastructure;

/// <summary>Implémentations des ports des projections. La session et les magasins sont par portée (une transaction).</summary>
public static class ProjectionInfrastructureExtensions
{
    /// <summary>
    /// Projections PostgreSQL. La chaîne de connexion désigne le rôle <c>ledger_projection</c>
    /// (<c>SSL Mode=VerifyFull</c> en AWS, authentification IAM sans mot de passe, ADR 0029) ;
    /// elle n'est lue qu'à la première résolution de la source de données.
    /// </summary>
    public static IServiceCollection AddPostgresProjectionStore(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        services.AddSingleton(sp => SourceDonneesPostgres.Creer(sp, connectionString(sp)));
        services.AddSingleton<IJournalReader, NpgsqlJournalReader>();
        services.AddSingleton<IConsumerCheckpointReader, NpgsqlConsumerCheckpointReader>();
        services.AddScoped<NpgsqlProjectionSession>();
        services.AddScoped<IProjectionSession>(sp => sp.GetRequiredService<NpgsqlProjectionSession>());
        services.AddScoped<IAppliedEventLog>(sp => sp.GetRequiredService<NpgsqlProjectionSession>());
        services.AddScoped<IConsumerCheckpoints>(sp => sp.GetRequiredService<NpgsqlProjectionSession>());
        services.AddScoped<IProjectionRebuildStore, NpgsqlProjectionRebuildStore>();
        services.AddScoped<IAccountBalanceStore, NpgsqlAccountBalanceStore>();
        services.AddScoped<ILedgerEntryStore, NpgsqlLedgerEntryStore>();
        services.AddScoped<IChartOfAccountsStore, NpgsqlChartOfAccountsStore>();
        services.AddScoped<IPeriodStatusStore, NpgsqlPeriodStatusStore>();
        services.AddScoped<IPeriodSnapshotStore, NpgsqlPeriodSnapshotStore>();
        services.AddScoped<ITaxProjectionStore, NpgsqlTaxProjectionStore>();
        return services;
    }

    /// <summary>Projections en mémoire, perdues à l'arrêt : développement et tests uniquement.</summary>
    public static IServiceCollection AddInMemoryProjectionStore(this IServiceCollection services, InMemoryJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        services.AddSingleton(journal);
        services.AddSingleton<IJournalReader>(journal);
        services.AddSingleton<InMemoryProjectionDatabase>();
        services.AddSingleton<IConsumerCheckpointReader, InMemoryConsumerCheckpointReader>();
        services.AddScoped<InMemoryProjectionSession>();
        services.AddScoped<IProjectionSession>(sp => sp.GetRequiredService<InMemoryProjectionSession>());
        services.AddScoped<IAppliedEventLog>(sp => sp.GetRequiredService<InMemoryProjectionSession>());
        services.AddScoped<IConsumerCheckpoints>(sp => sp.GetRequiredService<InMemoryProjectionSession>());
        services.AddScoped<IProjectionRebuildStore, InMemoryProjectionRebuildStore>();
        services.AddScoped<IAccountBalanceStore, InMemoryAccountBalanceStore>();
        services.AddScoped<ILedgerEntryStore, InMemoryLedgerEntryStore>();
        services.AddScoped<IChartOfAccountsStore, InMemoryChartOfAccountsStore>();
        services.AddScoped<IPeriodStatusStore, InMemoryPeriodStatusStore>();
        services.AddScoped<IPeriodSnapshotStore, InMemoryPeriodSnapshotStore>();
        services.AddScoped<ITaxProjectionStore, InMemoryTaxProjectionStore>();
        return services;
    }

    /// <summary>Source Kafka des événements publiés par l'outbox.</summary>
    public static IServiceCollection AddKafkaEventSource(this IServiceCollection services, Func<IServiceProvider, KafkaSourceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton<IEventSource>(sp => new KafkaEventSource(options(sp), sp.GetRequiredService<IConsumerCheckpointReader>()));
        return services;
    }
}
