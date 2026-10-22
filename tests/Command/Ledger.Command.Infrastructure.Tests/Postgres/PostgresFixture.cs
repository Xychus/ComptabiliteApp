using Ledger.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ledger.Command.Infrastructure.Tests.Postgres;

/// <summary>
/// PostgreSQL éphémère (Testcontainers), migré une fois pour toute la collection. L'event store
/// s'y connecte avec le rôle <c>ledger_app</c> (moindre privilège), qui reçoit un mot de passe
/// aléatoire propre à l'exécution ; le rôle propriétaire sert aux vérifications hors du rôle applicatif.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Même version majeure que la cible Aurora (point P4 du plan).</summary>
    public const string Image = "postgres:18-alpine";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    public NpgsqlDataSource AppDataSource { get; private set; } = null!;

    public NpgsqlDataSource OwnerDataSource { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);
        var migration = LedgerMigrator.Upgrade(_container.GetConnectionString(), logToConsole: false);
        if (!migration.Successful)
        {
            throw new InvalidOperationException("Échec des migrations.", migration.Error);
        }

        OwnerDataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        var password = Guid.NewGuid().ToString("N");
        await using (var command = OwnerDataSource.CreateCommand($"ALTER ROLE ledger_app LOGIN PASSWORD '{password}'"))
        {
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        AppDataSource = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Username = "ledger_app", Password = password }.ConnectionString);
    }

    public async ValueTask DisposeAsync()
    {
        if (AppDataSource is not null)
        {
            await AppDataSource.DisposeAsync().ConfigureAwait(false);
        }

        if (OwnerDataSource is not null)
        {
            await OwnerDataSource.DisposeAsync().ConfigureAwait(false);
        }

        await _container.DisposeAsync().ConfigureAwait(false);
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
