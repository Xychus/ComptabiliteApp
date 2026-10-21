using Ledger.Projections.Application;
using Ledger.Projections.Application.Processing;
using Ledger.Projections.Application.Rebuild;
using Ledger.Projections.Infrastructure;
using Ledger.Projections.Infrastructure.InMemory;
using Ledger.Projections.Infrastructure.Kafka;
using Ledger.Projections.Worker.Composition;
using Microsoft.Extensions.Options;

namespace Ledger.Projections.Worker;

/// <summary>
/// Composition du worker de projections. Deux modes : consommation continue de Kafka (défaut),
/// ou reconstruction d'une projection (<c>rebuild &lt;projection&gt;</c>, tâche ponctuelle, ADR 0016).
/// </summary>
public static class ProjectionWorkerHosting
{
    /// <summary>Chaîne de connexion du rôle <c>ledger_projection</c>, injectée depuis Secrets Manager.</summary>
    public const string ConnectionStringName = "Projections";

    /// <summary>Fournisseur de stockage : <c>Postgres</c> (défaut) ou <c>InMemory</c> (développement).</summary>
    public const string ProviderKey = "Ledger:Projections:Provider";

    public const string InMemoryProvider = "InMemory";

    public const string RebuildCommand = "rebuild";

    public static HostApplicationBuilder AddProjectionWorker(this HostApplicationBuilder builder, bool consume)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(console =>
        {
            console.IncludeScopes = true;
            console.UseUtcTimestamp = true;
            console.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffK";
        });

        var services = builder.Services;
        var tax = builder.Configuration.GetSection(TaxAccountsOptions.Section).Get<TaxAccountsOptions>() ?? new TaxAccountsOptions();
        services.AddLedgerProjections(tax.ToMap());

        if (string.Equals(builder.Configuration[ProviderKey], InMemoryProvider, StringComparison.Ordinal))
        {
            if (!builder.Environment.IsDevelopment())
            {
                throw new InvalidOperationException($"Les projections en mémoire ({ProviderKey}={InMemoryProvider}) sont réservées au développement.");
            }

            services.AddInMemoryProjectionStore(new InMemoryJournal());
        }
        else
        {
            var connectionString = builder.Configuration.GetConnectionString(ConnectionStringName);
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException($"Chaîne de connexion « {ConnectionStringName} » absente.");
            }

            services.AddPostgresProjectionStore(_ => connectionString);
        }

        if (consume)
        {
            services.AddOptions<KafkaSourceOptions>()
                .Bind(builder.Configuration.GetSection(KafkaSourceOptions.Section))
                .ValidateDataAnnotations()
                .ValidateOnStart();
            services.AddKafkaEventSource(sp => sp.GetRequiredService<IOptions<KafkaSourceOptions>>().Value);
            services.AddHostedService<ProjectionConsumerService>();
        }

        return builder;
    }

    /// <summary>Reconstruit la projection nommée, puis rend la main (code de sortie 0).</summary>
    public static async Task<int> RunRebuildAsync(IHost host, string projection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!ProjectionNames.All.Contains(projection, StringComparer.Ordinal))
        {
            await Console.Error.WriteLineAsync($"Projection inconnue : {projection}. Projections : {string.Join(", ", ProjectionNames.All)}.").ConfigureAwait(false);
            return 2;
        }

        await host.Services.GetRequiredService<ProjectionRebuilder>().RebuildAsync(projection, cancellationToken).ConfigureAwait(false);
        return 0;
    }
}

/// <summary>
/// Consommation continue. La boucle bloque sur Kafka : elle tourne sur un fil dédié. Une
/// exception arrête l'hôte (comportement par défaut) ; l'orchestrateur relance le worker, qui
/// reprend au dernier checkpoint validé.
/// </summary>
public sealed class ProjectionConsumerService(ProjectionPump pump, IEventSource source) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        Task.Factory.StartNew(() => pump.RunAsync(source, stoppingToken), stoppingToken, TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
}
