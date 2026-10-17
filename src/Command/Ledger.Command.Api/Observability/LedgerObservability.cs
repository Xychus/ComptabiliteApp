using System.Reflection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Ledger.Command.Api.Observability;

/// <summary>
/// Journalisation structurée (JSON sur la sortie standard, portées incluses) et OpenTelemetry
/// (traces, métriques, journaux). L'export OTLP n'est activé que si
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> est défini (collecteur ADOT en phase 8).
/// </summary>
public static class LedgerObservability
{
    public const string ServiceName = "ledger-command-api";

    /// <summary>Sources de traces et compteurs natifs de Npgsql.</summary>
    private const string NpgsqlInstrumentation = "Npgsql";

    public static WebApplicationBuilder AddLedgerObservability(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(console =>
        {
            console.IncludeScopes = true;
            console.UseUtcTimestamp = true;
            console.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffK";
        });

        var version = typeof(LedgerObservability).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var telemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(ServiceName, serviceVersion: version))
            .WithTracing(tracing => tracing.AddAspNetCoreInstrumentation().AddSource(NpgsqlInstrumentation))
            .WithMetrics(metrics => metrics.AddAspNetCoreInstrumentation().AddMeter(NpgsqlInstrumentation))
            .WithLogging();
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetry.UseOtlpExporter();
        }

        return builder;
    }
}

/// <summary>
/// Sondes : <c>/health/live</c> (le processus répond, aucune dépendance) et <c>/health/ready</c>
/// (le journal PostgreSQL répond). Anonymes : elles ne révèlent que l'état.
/// </summary>
public static class LedgerHealthChecks
{
    public const string ReadyTag = "ready";
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";

    public static IEndpointRouteBuilder MapLedgerHealthChecks(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions { Predicate = check => check.Tags.Contains(ReadyTag) }).AllowAnonymous();
        return endpoints;
    }
}
