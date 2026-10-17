using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace Ledger.Command.Infrastructure.Postgres;

/// <summary>Disponibilité du journal : une connexion s'ouvre et la base répond, avec le rôle applicatif.</summary>
public sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var command = dataSource.CreateCommand("SELECT 1");
            await using var commandScope = command.ConfigureAwait(false);
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return HealthCheckResult.Healthy();
        }
        catch (NpgsqlException exception)
        {
            return HealthCheckResult.Unhealthy("Journal PostgreSQL indisponible.", exception);
        }
    }
}
