using Ledger.Command.Api.Tests.Support;
using Ledger.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ledger.Command.Api.Tests.Postgres;

/// <summary>
/// API de commandes sur un PostgreSQL éphémère migré, connectée avec le rôle <c>ledger_app</c>
/// (mot de passe aléatoire propre à l'exécution) ; le rôle propriétaire sert aux vérifications.
/// </summary>
public sealed class PostgresApi : IAsyncLifetime
{
    /// <summary>Même image que les tests de l'event store (point P4 du plan).</summary>
    public const string Image = "postgres:17.6-alpine";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    public LedgerApiFactory Factory { get; private set; } = null!;

    public NpgsqlDataSource Owner { get; private set; } = null!;

    public LedgerClient NewClient() => new(Factory.CreateAuthenticatedClient());

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync().ConfigureAwait(false);
        var migration = LedgerMigrator.Upgrade(_container.GetConnectionString(), logToConsole: false);
        if (!migration.Successful)
        {
            throw new InvalidOperationException("Échec des migrations.", migration.Error);
        }

        Owner = NpgsqlDataSource.Create(_container.GetConnectionString());
        var password = Guid.NewGuid().ToString("N");
        await using (var command = Owner.CreateCommand($"ALTER ROLE ledger_app LOGIN PASSWORD '{password}'"))
        {
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        var appConnectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Username = "ledger_app", Password = password }.ConnectionString;
        Factory = new LedgerApiFactory(appConnectionString);
    }

    public async Task<long> CountAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = Owner.CreateCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken).ConfigureAwait(false))!;
    }

    public async ValueTask DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync().ConfigureAwait(false);
        }

        if (Owner is not null)
        {
            await Owner.DisposeAsync().ConfigureAwait(false);
        }

        await _container.DisposeAsync().ConfigureAwait(false);
    }
}
