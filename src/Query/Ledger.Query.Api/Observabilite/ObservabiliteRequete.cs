using System.Reflection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Ledger.Query.Api.Observabilite;

/// <summary>
/// Journalisation structurée (JSON sur la sortie standard, portées incluses) et OpenTelemetry
/// (traces, métriques, journaux). L'export OTLP n'est activé que si
/// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> est défini (collecteur ADOT en phase 8).
/// </summary>
public static class ObservabiliteRequete
{
    public const string NomService = "ledger-query-api";

    /// <summary>Sources de traces et compteurs natifs de Npgsql.</summary>
    private const string InstrumentationNpgsql = "Npgsql";

    public static WebApplicationBuilder AjouterObservabilite(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(console =>
        {
            console.IncludeScopes = true;
            console.UseUtcTimestamp = true;
            console.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffK";
        });

        var version = typeof(ObservabiliteRequete).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var telemetrie = builder.Services.AddOpenTelemetry()
            .ConfigureResource(ressource => ressource.AddService(NomService, serviceVersion: version))
            .WithTracing(traces => traces.AddAspNetCoreInstrumentation().AddSource(InstrumentationNpgsql))
            .WithMetrics(metriques => metriques.AddAspNetCoreInstrumentation().AddMeter(InstrumentationNpgsql))
            .WithLogging();
        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            telemetrie.UseOtlpExporter();
        }

        return builder;
    }
}

/// <summary>
/// Sondes : <c>/health/live</c> (le processus répond, aucune dépendance) et <c>/health/ready</c>
/// (la base des projections répond). Anonymes : elles ne révèlent que l'état.
/// </summary>
public static class SondesSante
{
    public const string EtiquetteDisponible = "ready";
    public const string CheminVivant = "/health/live";
    public const string CheminDisponible = "/health/ready";

    public static IEndpointRouteBuilder AssocierSondesSante(this IEndpointRouteBuilder routes)
    {
        routes.MapHealthChecks(CheminVivant, new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
        routes.MapHealthChecks(CheminDisponible, new HealthCheckOptions { Predicate = sonde => sonde.Tags.Contains(EtiquetteDisponible) }).AllowAnonymous();
        return routes;
    }
}
