using Npgsql;
using Testcontainers.PostgreSql;

namespace Ledger.Migrations.Tests;

/// <summary>
/// PostgreSQL éphémère (Testcontainers), migré une fois pour toute la collection. Le rôle
/// <c>ledger_app</c> y reçoit une connexion avec un mot de passe aléatoire, propre à l'exécution.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Même version majeure que la cible Aurora (point P4 du plan).</summary>
    public const string Image = "postgres:18-alpine";

    // Réplication logique activée, comme Aurora (rds.logical_replication) : slots de la purge de l'outbox.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).WithCommand("-c", "wal_level=logical").Build();

    public string OwnerConnectionString => _container.GetConnectionString();

    public string AppConnectionString { get; private set; } = string.Empty;

    /// <summary>Rôle <c>ledger_maintenance</c> (purge de l'outbox), mot de passe aléatoire.</summary>
    public string ChaineMaintenance { get; private set; } = string.Empty;

    public MigrationResult FirstRun { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);
        FirstRun = LedgerMigrator.Upgrade(OwnerConnectionString, logToConsole: false);
        var password = Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(OwnerConnectionString))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand($"ALTER ROLE ledger_app LOGIN PASSWORD '{password}'", connection);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        AppConnectionString = new NpgsqlConnectionStringBuilder(OwnerConnectionString) { Username = "ledger_app", Password = password }.ConnectionString;

        var motDePasseMaintenance = Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(OwnerConnectionString))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand($"ALTER ROLE ledger_maintenance LOGIN PASSWORD '{motDePasseMaintenance}'", connection);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        ChaineMaintenance = new NpgsqlConnectionStringBuilder(OwnerConnectionString) { Username = "ledger_maintenance", Password = motDePasseMaintenance }.ConnectionString;
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class PostgresGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
