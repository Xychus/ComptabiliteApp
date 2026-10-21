using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Ledger.Query.Infrastructure.Postgres;

/// <summary>Sonde de disponibilité : la base des projections répond.</summary>
public sealed class VerificationSantePostgres(NpgsqlDataSource source) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var commande = source.CreateCommand("SELECT 1");
            await using var portee = commande.ConfigureAwait(false);
            await commande.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (NpgsqlException exception)
        {
            return HealthCheckResult.Unhealthy("Base des projections PostgreSQL indisponible.", exception);
        }
    }
}
