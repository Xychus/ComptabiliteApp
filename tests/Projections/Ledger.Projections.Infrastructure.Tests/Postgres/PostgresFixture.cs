using Ledger.Contracts.Events.V1;
using Ledger.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ledger.Projections.Infrastructure.Tests.Postgres;

/// <summary>
/// PostgreSQL éphémère (Testcontainers), migré une fois pour toute la collection. Les projections
/// s'y connectent avec le rôle <c>ledger_projection</c> (mot de passe aléatoire propre à
/// l'exécution) ; le rôle propriétaire n'écrit que le journal, comme le ferait le service de commandes.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Même version majeure que la cible Aurora (point P4 du plan).</summary>
    public const string Image = "postgres:17.6-alpine";

    private const string InsertEventSql =
        """
        INSERT INTO ledger.events (event_id, stream_id, stream_version, event_type, schema_version, entity_id, business_date,
            recorded_at, period, payload, balance_after, correlation_id, causation_id, actor, hash, previous_hash)
        VALUES (@event_id, @stream_id, @stream_version, @event_type, @schema_version, @entity_id, @business_date,
            @recorded_at, @period, @payload::jsonb, @balance_after::numeric, @correlation_id, @causation_id, @actor, @hash, @previous_hash)
        RETURNING global_sequence
        """;

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(Image).Build();

    public NpgsqlDataSource OwnerDataSource { get; private set; } = null!;

    public string ProjectionConnectionString { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        var migration = LedgerMigrator.Upgrade(_container.GetConnectionString(), logToConsole: false);
        if (!migration.Successful)
        {
            throw new InvalidOperationException("Échec des migrations.", migration.Error);
        }

        OwnerDataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        var password = Guid.NewGuid().ToString("N");
        await using (var command = OwnerDataSource.CreateCommand($"ALTER ROLE ledger_projection LOGIN PASSWORD '{password}'"))
        {
            await command.ExecuteNonQueryAsync();
        }

        ProjectionConnectionString = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Username = "ledger_projection", Password = password }.ConnectionString;
    }

    /// <summary>Insère l'événement dans <c>ledger.events</c> ; la séquence vient du trigger.</summary>
    public async Task<EventEnvelopeV1> AppendAsync(EventEnvelopeV1 envelope, NpgsqlConnection? connection = null, NpgsqlTransaction? transaction = null)
    {
        await using var command = connection is null ? OwnerDataSource.CreateCommand(InsertEventSql) : new NpgsqlCommand(InsertEventSql, connection, transaction);
        command.Parameters.AddWithValue("event_id", envelope.EventId);
        command.Parameters.AddWithValue("stream_id", envelope.StreamId);
        command.Parameters.AddWithValue("stream_version", envelope.StreamVersion);
        command.Parameters.AddWithValue("event_type", envelope.EventType);
        command.Parameters.AddWithValue("schema_version", envelope.SchemaVersion);
        command.Parameters.AddWithValue("entity_id", envelope.EntityId);
        command.Parameters.AddWithValue("business_date", (object?)envelope.BusinessDate ?? DBNull.Value);
        command.Parameters.AddWithValue("recorded_at", envelope.RecordedAt);
        command.Parameters.AddWithValue("period", envelope.Period);
        command.Parameters.AddWithValue("payload", envelope.Payload.GetRawText());
        command.Parameters.AddWithValue("balance_after", (object?)envelope.BalanceAfter ?? DBNull.Value);
        command.Parameters.AddWithValue("correlation_id", envelope.CorrelationId);
        command.Parameters.AddWithValue("causation_id", (object?)envelope.CausationId ?? DBNull.Value);
        command.Parameters.AddWithValue("actor", envelope.Actor);
        command.Parameters.AddWithValue("hash", envelope.Hash);
        command.Parameters.AddWithValue("previous_hash", (object?)envelope.PreviousHash ?? DBNull.Value);
        var sequence = (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        return envelope with { GlobalSequence = sequence };
    }

    public async ValueTask DisposeAsync()
    {
        if (OwnerDataSource is not null)
        {
            await OwnerDataSource.DisposeAsync();
        }

        await _container.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class PostgresGroup : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
