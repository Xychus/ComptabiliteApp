using System.Globalization;
using System.Text.Json;
using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Rebuild;
using Npgsql;

namespace Ledger.Projections.Infrastructure.Postgres;

/// <summary>
/// Lecture de <c>ledger.events</c> avec le rôle <c>ledger_projection</c> (<c>SELECT</c> seul) :
/// les enveloppes sont celles que publie l'outbox (ADR 0007).
/// </summary>
public sealed class NpgsqlJournalReader(NpgsqlDataSource dataSource) : IJournalReader
{
    internal const string EnvelopeColumns =
        """
        e.global_sequence, e.event_id, e.stream_id, e.stream_version, e.event_type, e.schema_version, e.entity_id,
        e.business_date, e.recorded_at, e.period, e.balance_after, e.correlation_id, e.causation_id, e.actor, e.hash,
        e.previous_hash, e.payload::text
        """;

    private const string ReadAfterSql =
        $"""
        SELECT {EnvelopeColumns}
        FROM ledger.events e
        WHERE e.global_sequence > @after
        ORDER BY e.global_sequence
        LIMIT @max_count
        """;

    private const string ReadPeriodEventsSql =
        $"""
        SELECT {EnvelopeColumns}
        FROM ledger.events e
        WHERE e.entity_id = @entity_id AND e.period = @period AND e.event_type = ANY(@event_types)
        ORDER BY e.global_sequence
        """;

    public async Task<IReadOnlyList<EventEnvelopeV1>> ReadAfterAsync(long afterSequence, int maxCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxCount, 1);
        var command = dataSource.CreateCommand(ReadAfterSql);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("after", afterSequence);
            command.Parameters.AddWithValue("max_count", maxCount);
            return await ReadEnvelopesAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<IReadOnlyList<EventEnvelopeV1>> ReadPeriodEventsAsync(
        string entityId,
        string period,
        IReadOnlyCollection<string> eventTypes,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);
        var command = dataSource.CreateCommand(ReadPeriodEventsSql);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("entity_id", entityId);
            command.Parameters.AddWithValue("period", period);
            command.Parameters.AddWithValue("event_types", eventTypes.ToArray());
            return await ReadEnvelopesAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Lit des lignes dont les colonnes suivent <see cref="EnvelopeColumns"/>.</summary>
    internal static async Task<IReadOnlyList<EventEnvelopeV1>> ReadEnvelopesAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            var envelopes = new List<EventEnvelopeV1>();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                envelopes.Add(ReadEnvelope(reader));
            }

            return envelopes;
        }
    }

    private static EventEnvelopeV1 ReadEnvelope(NpgsqlDataReader reader)
    {
        using var payload = JsonDocument.Parse(reader.GetString(16));
        return new EventEnvelopeV1(
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
            reader.IsDBNull(10) ? null : reader.GetDecimal(10).ToString("0.00", CultureInfo.InvariantCulture),
            reader.GetGuid(11),
            reader.IsDBNull(12) ? null : reader.GetGuid(12),
            reader.GetString(13),
            reader.GetString(14),
            reader.IsDBNull(15) ? null : reader.GetString(15),
            payload.RootElement.Clone());
    }
}
