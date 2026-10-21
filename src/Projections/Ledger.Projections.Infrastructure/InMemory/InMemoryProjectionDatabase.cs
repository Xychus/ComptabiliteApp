using System.Collections.Immutable;
using Ledger.Projections.Application.Events;
using Ledger.Projections.Application.Processing;

namespace Ledger.Projections.Infrastructure.InMemory;

/// <summary>
/// Base de projections en mémoire, pour les tests et le développement : un dictionnaire immuable
/// par table, mêmes noms que le schéma <c>projections</c>. Les transactions sont sérialisées
/// (un verrou global, l'équivalent des verrous consultatifs) et publiées d'un bloc à la validation.
/// </summary>
public sealed class InMemoryProjectionDatabase : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ImmutableDictionary<string, object> _tables = ImmutableDictionary.Create<string, object>(StringComparer.Ordinal);

    /// <summary>Contenu validé d'une table physique (vide si elle n'existe pas).</summary>
    public ImmutableDictionary<TKey, TRow> Rows<TKey, TRow>(string physicalTable)
        where TKey : notnull =>
        Volatile.Read(ref _tables).TryGetValue(physicalTable, out var rows) ? (ImmutableDictionary<TKey, TRow>)rows : [];

    public void Dispose() => _gate.Dispose();

    internal async Task<ImmutableDictionary<string, object>> AcquireAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return Volatile.Read(ref _tables);
    }

    internal void Publish(ImmutableDictionary<string, object> tables) => Volatile.Write(ref _tables, tables);

    internal void Release() => _gate.Release();
}

/// <summary>Session en mémoire : copie de travail des tables, publiée à la validation, abandonnée sinon.</summary>
public sealed class InMemoryProjectionSession(InMemoryProjectionDatabase database) : IProjectionSession, IAppliedEventLog, IConsumerCheckpoints, IAsyncDisposable
{
    private ImmutableDictionary<string, object>? _working;

    public ProjectionTarget Target { get; private set; } = ProjectionTarget.Live;

    public async Task BeginAsync(ProjectionTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (_working is not null)
        {
            throw new InvalidOperationException("Session déjà commencée.");
        }

        Target = target;
        _working = await database.AcquireAsync(cancellationToken).ConfigureAwait(false);
    }

    public Task CommitAsync(CancellationToken cancellationToken)
    {
        database.Publish(Working);
        _working = null;
        database.Release();
        return Task.CompletedTask;
    }

    public Task<bool> TryMarkAppliedAsync(string projection, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var applied = Rows<Guid, long>(ProjectionTables.Applied(projection));
        if (applied.ContainsKey(context.EventId))
        {
            return Task.FromResult(false);
        }

        Save(ProjectionTables.Applied(projection), applied.Add(context.EventId, context.GlobalSequence));
        return Task.FromResult(true);
    }

    public Task AdvanceAsync(string projection, EventContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var slot = ProjectionTables.Slot(projection, Target);
        var positions = PhysicalRows<string, ProjectionPosition>(ProjectionTables.Positions);
        if (!positions.TryGetValue(slot, out var current) || context.GlobalSequence > current.ThroughSequence)
        {
            SavePhysical(ProjectionTables.Positions, positions.SetItem(slot, new ProjectionPosition(context.GlobalSequence, context.RecordedAt)));
        }

        return Task.CompletedTask;
    }

    public Task SaveAsync(SourcePosition position, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(position);
        var offsets = PhysicalRows<ConsumerOffsetKey, long>(ProjectionTables.ConsumerOffsets);
        SavePhysical(ProjectionTables.ConsumerOffsets, offsets.SetItem(new ConsumerOffsetKey(position.ConsumerGroup, position.Topic, position.Partition), position.Offset + 1));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_working is not null)
        {
            _working = null;
            database.Release();
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Lignes d'une table, selon la cible de la session.</summary>
    internal ImmutableDictionary<TKey, TRow> Rows<TKey, TRow>(string table)
        where TKey : notnull => PhysicalRows<TKey, TRow>(ProjectionTables.Physical(table, Target));

    internal void Save<TKey, TRow>(string table, ImmutableDictionary<TKey, TRow> rows)
        where TKey : notnull => SavePhysical(ProjectionTables.Physical(table, Target), rows);

    internal ImmutableDictionary<TKey, TRow> PhysicalRows<TKey, TRow>(string physicalTable)
        where TKey : notnull =>
        Working.TryGetValue(physicalTable, out var rows) ? (ImmutableDictionary<TKey, TRow>)rows : [];

    internal object? PhysicalTable(string physicalTable) => Working.GetValueOrDefault(physicalTable);

    internal void SavePhysical(string physicalTable, object rows) => _working = Working.SetItem(physicalTable, rows);

    internal void Drop(string physicalTable) => _working = Working.Remove(physicalTable);

    private ImmutableDictionary<string, object> Working => _working ?? throw new InvalidOperationException("Session non commencée.");
}

/// <summary>Ligne de <c>projection_positions</c>.</summary>
public sealed record ProjectionPosition(long ThroughSequence, DateTimeOffset LastRecordedAt);

/// <summary>Clé de <c>consumer_offsets</c>.</summary>
public sealed record ConsumerOffsetKey(string ConsumerGroup, string Topic, int Partition);
