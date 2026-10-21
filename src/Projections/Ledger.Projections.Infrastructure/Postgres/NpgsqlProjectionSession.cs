using System.Data;
using Ledger.Projections.Application.Events;
using Ledger.Projections.Application.Processing;
using Npgsql;

namespace Ledger.Projections.Infrastructure.Postgres;

/// <summary>
/// Transaction <c>READ COMMITTED</c> des projections, une par portée (ADR 0015) : modèles de
/// lecture, événements appliqués, positions et checkpoint du consommateur.
/// En service, chaque projection touchée prend un verrou consultatif partagé jusqu'à la fin de
/// la transaction ; la bascule d'une reconstruction le prend en exclusif (ADR 0016).
/// Les positions sont cumulées en mémoire et écrites une fois, à la validation.
/// </summary>
public sealed class NpgsqlProjectionSession(NpgsqlDataSource dataSource) : IProjectionSession, IAppliedEventLog, IConsumerCheckpoints, IAsyncDisposable
{
    /// <summary>Préfixe de la clé du verrou consultatif d'une projection.</summary>
    internal const string LockKeyPrefix = "ledger.projections/";

    internal const string LockSql = "SELECT pg_advisory_xact_lock(hashtextextended(@key, 0))";

    private const string SharedLockSql = "SELECT pg_advisory_xact_lock_shared(hashtextextended(@key, 0))";

    private const string SaveOffsetSql =
        """
        INSERT INTO projections.consumer_offsets (consumer_group, topic, partition, next_offset)
        VALUES (@consumer_group, @topic, @partition, @next_offset)
        ON CONFLICT (consumer_group, topic, partition) DO UPDATE SET next_offset = EXCLUDED.next_offset
        """;

    private const string SavePositionSql =
        """
        INSERT INTO projections.projection_positions AS p (slot, through_sequence, last_recorded_at)
        VALUES (@slot, @through_sequence, @last_recorded_at)
        ON CONFLICT (slot) DO UPDATE SET
            through_sequence = GREATEST(p.through_sequence, EXCLUDED.through_sequence),
            last_recorded_at = CASE WHEN EXCLUDED.through_sequence > p.through_sequence THEN EXCLUDED.last_recorded_at ELSE p.last_recorded_at END
        """;

    private readonly HashSet<string> _locked = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EventContext> _positions = new(StringComparer.Ordinal);
    private NpgsqlConnection? _connection;
    private NpgsqlTransaction? _transaction;

    public ProjectionTarget Target { get; private set; } = ProjectionTarget.Live;

    public async Task BeginAsync(ProjectionTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_connection is not null)
        {
            throw new InvalidOperationException("Session déjà commencée.");
        }

        Target = target;
        _connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        _transaction = await _connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        foreach (var (slot, context) in _positions)
        {
            await ExecuteAsync(
                SavePositionSql,
                parameters =>
                {
                    parameters.AddWithValue("slot", slot);
                    parameters.AddWithValue("through_sequence", context.GlobalSequence);
                    parameters.AddWithValue("last_recorded_at", context.RecordedAt);
                },
                cancellationToken).ConfigureAwait(false);
        }

        await Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        _positions.Clear();
    }

    public async Task<bool> TryMarkAppliedAsync(string projection, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await EnterAsync(projection, cancellationToken).ConfigureAwait(false);
        var inserted = await ExecuteAsync(
            $"INSERT INTO {Table(ProjectionTables.Applied(projection))} (event_id, global_sequence) VALUES (@event_id, @global_sequence) ON CONFLICT (event_id) DO NOTHING",
            parameters =>
            {
                parameters.AddWithValue("event_id", context.EventId);
                parameters.AddWithValue("global_sequence", context.GlobalSequence);
            },
            cancellationToken).ConfigureAwait(false);
        return inserted == 1;
    }

    public async Task AdvanceAsync(string projection, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        await EnterAsync(projection, cancellationToken).ConfigureAwait(false);
        var slot = ProjectionTables.Slot(projection, Target);
        if (!_positions.TryGetValue(slot, out var current) || context.GlobalSequence > current.GlobalSequence)
        {
            _positions[slot] = context;
        }
    }

    public async Task SaveAsync(SourcePosition position, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(position);
        await ExecuteAsync(
            SaveOffsetSql,
            parameters =>
            {
                parameters.AddWithValue("consumer_group", position.ConsumerGroup);
                parameters.AddWithValue("topic", position.Topic);
                parameters.AddWithValue("partition", position.Partition);
                parameters.AddWithValue("next_offset", position.Offset + 1);
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
        }

        if (_connection is not null)
        {
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Nom qualifié et protégé d'une table, selon la cible de la session.</summary>
    internal string Table(string table) => Qualified(ProjectionTables.Physical(table, Target));

    internal static string Qualified(string physicalTable) => $"{ProjectionTables.Schema}.\"{physicalTable}\"";

    internal NpgsqlCommand CreateCommand(string sql) => new(sql, Connection, Transaction);

    /// <summary>Exécute une instruction dans la transaction ; renvoie le nombre de lignes touchées.</summary>
    internal async Task<int> ExecuteAsync(string sql, Action<NpgsqlParameterCollection> bind, CancellationToken cancellationToken)
    {
        var command = CreateCommand(sql);
        await using (command.ConfigureAwait(false))
        {
            bind(command.Parameters);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    internal NpgsqlBatch CreateBatch() => new(Connection, Transaction);

    internal NpgsqlConnection Connection => _connection ?? throw new InvalidOperationException("Session non commencée.");

    internal NpgsqlTransaction Transaction => _transaction ?? throw new InvalidOperationException("Session non commencée.");

    /// <summary>Verrou partagé de la projection, une fois par transaction, pour les tables en service.</summary>
    private async Task EnterAsync(string projection, CancellationToken cancellationToken)
    {
        if (Target.IsRebuild || !_locked.Add(projection))
        {
            return;
        }

        await ExecuteAsync(SharedLockSql, parameters => parameters.AddWithValue("key", LockKeyPrefix + projection), cancellationToken).ConfigureAwait(false);
    }
}
