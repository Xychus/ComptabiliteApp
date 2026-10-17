using System.Text.Json;
using Ledger.Command.Application.EventStore;
using Npgsql;

namespace Ledger.Command.Infrastructure.Postgres;

/// <summary>
/// Lecture du journal PostgreSQL : le flux est localisé par <c>stream_versions</c>, puis chaque
/// événement est lu dans sa partition (ADR 0007). Les hash d'une période sont lus dans la seule
/// partition de son année.
/// </summary>
public sealed class NpgsqlEventStore(NpgsqlDataSource dataSource) : IEventReader, IPeriodEventReader
{
    internal const string EventColumns =
        """
        e.global_sequence, e.event_id, e.stream_id, e.stream_version, e.event_type, e.schema_version, e.entity_id,
        e.business_date, e.recorded_at, e.period, e.payload::text, e.balance_after, e.correlation_id, e.causation_id,
        e.actor, e.hash, e.previous_hash
        """;

    private const string ReadStreamSql =
        $"""
        SELECT {EventColumns}
        FROM ledger.stream_versions s
        JOIN ledger.events e ON e.global_sequence = s.global_sequence AND e.period = s.period
        WHERE s.stream_id = @stream_id
        ORDER BY s.stream_version
        """;

    private const string ReadPeriodHashesSql =
        """
        SELECT hash
        FROM ledger.events
        WHERE entity_id = @entity_id AND period = @period
        ORDER BY global_sequence
        """;

    public async Task<IReadOnlyList<string>> ReadPeriodHashesAsync(string entityId, string period, CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand(ReadPeriodHashesSql);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("entity_id", entityId);
        command.Parameters.AddWithValue("period", period);
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        var hashes = new List<string>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            hashes.Add(reader.GetString(0));
        }

        return hashes;
    }

    public async Task<IReadOnlyList<StoredEvent>> ReadStreamAsync(string streamId, CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand(ReadStreamSql);
        await using var commandScope = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("stream_id", streamId);
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerScope = reader.ConfigureAwait(false);
        var events = new List<StoredEvent>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            events.Add(ReadEvent(reader));
        }

        return events;
    }

    /// <summary>Lit une ligne dont les colonnes suivent <see cref="EventColumns"/>.</summary>
    internal static StoredEvent ReadEvent(NpgsqlDataReader reader)
    {
        using var payload = JsonDocument.Parse(reader.GetString(10));
        return new StoredEvent(
            reader.GetInt64(0),
            reader.GetGuid(1),
            reader.GetString(2),
            reader.GetInt64(3),
            reader.GetString(4),
            reader.GetInt32(5),
            reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateOnly>(7),
            reader.GetFieldValue<DateTimeOffset>(8),
            reader.GetString(9),
            payload.RootElement.Clone(),
            reader.IsDBNull(11) ? null : reader.GetDecimal(11),
            new EventMetadata(reader.GetGuid(12), reader.IsDBNull(13) ? null : reader.GetGuid(13), reader.GetString(14)),
            reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16));
    }
}
