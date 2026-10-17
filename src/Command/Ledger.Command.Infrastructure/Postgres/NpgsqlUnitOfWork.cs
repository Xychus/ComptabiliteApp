using System.Data;
using Ledger.Command.Application.EventStore;
using Ledger.Command.Infrastructure.EventStore;
using Ledger.Command.Infrastructure.Outbox;
using Ledger.SharedKernel;
using Npgsql;
using NpgsqlTypes;

namespace Ledger.Command.Infrastructure.Postgres;

/// <summary>
/// Unité de travail PostgreSQL ; une instance par commande. La validation écrit, dans une seule
/// transaction <c>READ COMMITTED</c> : les événements (trigger de sceau, ADR 0005), puis leurs
/// lignes de <c>stream_versions</c>, <c>entry_ids</c> et <c>outbox</c> (ADR 0003 et 0007).
/// Les violations d'unicité deviennent des rejets ; rien n'est écrit en cas d'échec.
/// </summary>
public sealed class NpgsqlUnitOfWork(NpgsqlDataSource dataSource, IClock clock) : IEventAppender, IUnitOfWork
{
    private const string PeriodSealedState = "LG002";
    private const string EntryIdsPrimaryKey = "entry_ids_pkey";

    private const string ReadHeadsSql =
        """
        SELECT DISTINCT ON (stream_id) stream_id, stream_version, hash
        FROM ledger.stream_versions
        WHERE stream_id = ANY(@stream_ids)
        ORDER BY stream_id, stream_version DESC
        """;

    private const string InsertEventSql =
        """
        INSERT INTO ledger.events (event_id, stream_id, stream_version, event_type, schema_version, entity_id, business_date,
            recorded_at, period, payload, balance_after, correlation_id, causation_id, actor, hash, previous_hash)
        VALUES (@event_id, @stream_id, @stream_version, @event_type, @schema_version, @entity_id, @business_date,
            @recorded_at, @period, @payload, @balance_after, @correlation_id, @causation_id, @actor, @hash, @previous_hash)
        RETURNING global_sequence
        """;

    private const string InsertStreamVersionSql =
        """
        INSERT INTO ledger.stream_versions (stream_id, stream_version, event_id, global_sequence, period, hash)
        VALUES (@stream_id, @stream_version, @event_id, @global_sequence, @period, @hash)
        """;

    private const string InsertEntryIdSql = "INSERT INTO ledger.entry_ids (entry_id, event_id) VALUES (@entry_id, @event_id)";

    private const string InsertOutboxSql =
        "INSERT INTO ledger.outbox (event_id, topic, partition_key, payload) VALUES (@event_id, @topic, @partition_key, @payload)";

    private readonly List<EventToAppend> _staged = [];

    public void Append(EventToAppend eventToAppend)
    {
        ArgumentNullException.ThrowIfNull(eventToAppend);
        _staged.Add(eventToAppend);
    }

    public async Task<Result<IReadOnlyList<StoredEvent>>> CommitAsync(CancellationToken cancellationToken)
    {
        EventToAppend[] batch = [.. _staged];
        _staged.Clear();
        if (batch.Length == 0)
        {
            return Result.Success<IReadOnlyList<StoredEvent>>([]);
        }

        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await using var connectionScope = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        await using var transactionScope = transaction.ConfigureAwait(false);
        var heads = await ReadHeadsAsync(connection, transaction, batch, cancellationToken).ConfigureAwait(false);
        var prepared = AppendBatch.Prepare(batch, heads, clock.UtcNow);
        if (prepared.IsFailure)
        {
            return prepared;
        }

        try
        {
            var stored = await InsertEventsAsync(connection, transaction, prepared.Value, cancellationToken).ConfigureAwait(false);
            await InsertCompanionRowsAsync(connection, transaction, batch, stored, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return Result.Success(stored);
        }
        catch (PostgresException exception) when (Rejection(exception, batch) is { } rejection)
        {
            return Result.Failure<IReadOnlyList<StoredEvent>>(rejection);
        }
    }

    private static async Task<Dictionary<string, StreamHead>> ReadHeadsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, EventToAppend[] batch, CancellationToken cancellationToken)
    {
        using var command = new NpgsqlCommand(ReadHeadsSql, connection, transaction);
        command.Parameters.AddWithValue("stream_ids", batch.Select(e => e.StreamId).Distinct(StringComparer.Ordinal).ToArray());
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        var heads = new Dictionary<string, StreamHead>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            heads[reader.GetString(0)] = new StreamHead(reader.GetInt64(1), reader.GetString(2));
        }

        return heads;
    }

    private static async Task<IReadOnlyList<StoredEvent>> InsertEventsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, IReadOnlyList<StoredEvent> prepared, CancellationToken cancellationToken)
    {
        using var batch = new NpgsqlBatch(connection, transaction);
        foreach (var e in prepared)
        {
            var command = new NpgsqlBatchCommand(InsertEventSql);
            command.Parameters.AddWithValue("event_id", e.EventId);
            command.Parameters.AddWithValue("stream_id", e.StreamId);
            command.Parameters.AddWithValue("stream_version", e.StreamVersion);
            command.Parameters.AddWithValue("event_type", e.EventType);
            command.Parameters.AddWithValue("schema_version", e.SchemaVersion);
            command.Parameters.AddWithValue("entity_id", e.EntityId);
            command.Parameters.Add(Nullable("business_date", NpgsqlDbType.Date, e.BusinessDate));
            command.Parameters.AddWithValue("recorded_at", e.RecordedAt);
            command.Parameters.AddWithValue("period", e.Period);
            command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, e.Payload.GetRawText());
            command.Parameters.Add(Nullable("balance_after", NpgsqlDbType.Numeric, e.BalanceAfter));
            command.Parameters.AddWithValue("correlation_id", e.Metadata.CorrelationId);
            command.Parameters.Add(Nullable("causation_id", NpgsqlDbType.Uuid, e.Metadata.CausationId));
            command.Parameters.AddWithValue("actor", e.Metadata.Actor);
            command.Parameters.AddWithValue("hash", e.Hash);
            command.Parameters.Add(Nullable("previous_hash", NpgsqlDbType.Text, e.PreviousHash));
            batch.BatchCommands.Add(command);
        }

        var stored = new List<StoredEvent>(prepared.Count);
        var reader = await batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        foreach (var e in prepared)
        {
            await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            stored.Add(e with { GlobalSequence = reader.GetInt64(0) });
            await reader.NextResultAsync(cancellationToken).ConfigureAwait(false);
        }

        return stored;
    }

    /// <summary>
    /// Lignes compagnes, triées par clé pour que deux transactions concurrentes prennent les
    /// verrous d'unicité dans le même ordre (pas d'interblocage entre flux croisés).
    /// </summary>
    private static async Task InsertCompanionRowsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, EventToAppend[] batch, IReadOnlyList<StoredEvent> stored, CancellationToken cancellationToken)
    {
        using var npgsqlBatch = new NpgsqlBatch(connection, transaction);
        foreach (var e in stored.OrderBy(e => e.StreamId, StringComparer.Ordinal).ThenBy(e => e.StreamVersion))
        {
            var command = new NpgsqlBatchCommand(InsertStreamVersionSql);
            command.Parameters.AddWithValue("stream_id", e.StreamId);
            command.Parameters.AddWithValue("stream_version", e.StreamVersion);
            command.Parameters.AddWithValue("event_id", e.EventId);
            command.Parameters.AddWithValue("global_sequence", e.GlobalSequence);
            command.Parameters.AddWithValue("period", e.Period);
            command.Parameters.AddWithValue("hash", e.Hash);
            npgsqlBatch.BatchCommands.Add(command);
        }

        foreach (var (entryId, eventId) in batch.Where(e => e.ClaimedEntryId is not null).Select(e => (e.ClaimedEntryId!.Value, e.EventId)).OrderBy(c => c.Item1))
        {
            var command = new NpgsqlBatchCommand(InsertEntryIdSql);
            command.Parameters.AddWithValue("entry_id", entryId);
            command.Parameters.AddWithValue("event_id", eventId);
            npgsqlBatch.BatchCommands.Add(command);
        }

        foreach (var message in stored.Select(OutboxMessages.From))
        {
            var command = new NpgsqlBatchCommand(InsertOutboxSql);
            command.Parameters.AddWithValue("event_id", message.EventId);
            command.Parameters.AddWithValue("topic", message.Topic);
            command.Parameters.AddWithValue("partition_key", message.PartitionKey);
            command.Parameters.AddWithValue("payload", NpgsqlDbType.Jsonb, message.Payload.GetRawText());
            npgsqlBatch.BatchCommands.Add(command);
        }

        await npgsqlBatch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Traduit les erreurs attendues en rejets ; toute autre erreur reste une exception.
    /// Le détail des erreurs PostgreSQL étant masqué par Npgsql, l'identifiant ou la période en
    /// cause sont repris du lot (une écriture = une entité et une période).
    /// </summary>
    private static DomainError? Rejection(PostgresException exception, EventToAppend[] batch) => exception.SqlState switch
    {
        PostgresErrorCodes.UniqueViolation when exception.ConstraintName == EntryIdsPrimaryKey =>
            EventStoreErrors.EntryIdAlreadyUsed(batch.First(e => e.ClaimedEntryId is not null).ClaimedEntryId!.Value),
        PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure =>
            EventStoreErrors.ConcurrencyConflict(),
        PeriodSealedState => EventStoreErrors.PeriodSealed(batch[0].EntityId, batch[0].Period),
        _ => null,
    };

    private static NpgsqlParameter Nullable<T>(string name, NpgsqlDbType type, T? value)
        where T : struct =>
        new(name, type) { Value = value is { } present ? present : DBNull.Value };

    private static NpgsqlParameter Nullable(string name, NpgsqlDbType type, string? value) =>
        new(name, type) { Value = (object?)value ?? DBNull.Value };
}
