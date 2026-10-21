using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Processing;
using Ledger.Projections.Application.Rebuild;

namespace Ledger.Projections.Infrastructure.Postgres;

/// <summary>
/// Tables parallèles d'une reconstruction (ADR 0016) : créées par <c>LIKE … INCLUDING ALL</c>,
/// mises en service par renommage dans la transaction qui tient le verrou exclusif de la
/// projection. Le rôle <c>ledger_projection</c> est propriétaire du schéma <c>projections</c>.
/// </summary>
public sealed class NpgsqlProjectionRebuildStore(NpgsqlProjectionSession session) : IProjectionRebuildStore
{
    /// <summary>Rôle de l'API de requête, qui relit les tables mises en service.</summary>
    public const string QueryRole = "ledger_query";

    public async Task PrepareAsync(string projection, CancellationToken cancellationToken)
    {
        foreach (var table in ProjectionTables.Of(projection))
        {
            var rebuild = NpgsqlProjectionSession.Qualified(table + ProjectionTables.RebuildSuffix);
            await ExecuteAsync($"DROP TABLE IF EXISTS {rebuild}", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync($"CREATE TABLE {rebuild} (LIKE {NpgsqlProjectionSession.Qualified(table)} INCLUDING ALL)", cancellationToken).ConfigureAwait(false);
        }

        await session.ExecuteAsync(
            "DELETE FROM projections.projection_positions WHERE slot = @slot",
            parameters => parameters.AddWithValue("slot", ProjectionTables.Slot(projection, ProjectionTarget.Rebuild(projection))),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task LockLiveAsync(string projection, CancellationToken cancellationToken) =>
        await session.ExecuteAsync(
            NpgsqlProjectionSession.LockSql,
            parameters => parameters.AddWithValue("key", NpgsqlProjectionSession.LockKeyPrefix + projection),
            cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<EventEnvelopeV1>> ReadUnappliedAsync(string projection, IReadOnlyCollection<string> eventTypes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(eventTypes);
        var applied = NpgsqlProjectionSession.Qualified(ProjectionTables.Applied(projection) + ProjectionTables.RebuildSuffix);
        var command = session.CreateCommand(
            $"""
            SELECT {NpgsqlJournalReader.EnvelopeColumns}
            FROM ledger.events e
            WHERE e.event_type = ANY(@event_types)
              AND NOT EXISTS (SELECT 1 FROM {applied} a WHERE a.event_id = e.event_id)
            ORDER BY e.global_sequence
            """);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("event_types", eventTypes.ToArray());
            return await NpgsqlJournalReader.ReadEnvelopesAsync(command, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SwapAsync(string projection, CancellationToken cancellationToken)
    {
        foreach (var table in ProjectionTables.Of(projection))
        {
            var live = NpgsqlProjectionSession.Qualified(table);
            var retired = table + ProjectionTables.RetiredSuffix;
            await ExecuteAsync($"ALTER TABLE {live} RENAME TO \"{retired}\"", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync($"ALTER TABLE {NpgsqlProjectionSession.Qualified(table + ProjectionTables.RebuildSuffix)} RENAME TO \"{table}\"", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync($"DROP TABLE {NpgsqlProjectionSession.Qualified(retired)}", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync($"GRANT SELECT ON {live} TO {QueryRole}", cancellationToken).ConfigureAwait(false);
        }

        await session.ExecuteAsync(
            """
            DELETE FROM projections.projection_positions WHERE slot = @live;
            UPDATE projections.projection_positions SET slot = @live WHERE slot = @rebuild;
            """,
            parameters =>
            {
                parameters.AddWithValue("live", ProjectionTables.Slot(projection, ProjectionTarget.Live));
                parameters.AddWithValue("rebuild", ProjectionTables.Slot(projection, ProjectionTarget.Rebuild(projection)));
            },
            cancellationToken).ConfigureAwait(false);
    }

    private Task<int> ExecuteAsync(string sql, CancellationToken cancellationToken) => session.ExecuteAsync(sql, _ => { }, cancellationToken);
}
