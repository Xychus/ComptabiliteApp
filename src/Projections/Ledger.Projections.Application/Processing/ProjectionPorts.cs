using Ledger.Projections.Application.Events;

namespace Ledger.Projections.Application.Processing;

/// <summary>
/// Projection : modèle de lecture dérivé du journal, reconstructible par rejeu (invariant 2).
/// Une classe par projection ; elle implémente <see cref="IEventHandler{T}"/> pour chaque type
/// d'événement qui la concerne et n'écrit que par ses propres ports de stockage.
/// </summary>
public interface IProjection
{
    /// <summary>Nom stable : clé de l'idempotence, de la position et du rejeu (voir <see cref="ProjectionNames"/>).</summary>
    string Name { get; }
}

/// <summary>Noms des projections, identiques aux noms de leurs tables principales.</summary>
public static class ProjectionNames
{
    public const string AccountBalances = "account_balances";
    public const string LedgerEntries = "ledger_entries";
    public const string ChartOfAccounts = "chart_of_accounts";
    public const string Periods = "periods";
    public const string PeriodSnapshots = "period_snapshots";
    public const string TaxProjection = "tax_projection";

    public static IReadOnlyList<string> All { get; } =
        [AccountBalances, LedgerEntries, ChartOfAccounts, Periods, PeriodSnapshots, TaxProjection];
}

/// <summary>
/// Tables qu'une session écrit : les tables en service (<see cref="Live"/>), ou les tables
/// parallèles d'une projection en cours de reconstruction (ADR 0016).
/// </summary>
public sealed record ProjectionTarget
{
    private ProjectionTarget(string? rebuildOf) => RebuildOf = rebuildOf;

    public static ProjectionTarget Live { get; } = new((string?)null);

    /// <summary>Projection reconstruite, ou <see langword="null"/> pour les tables en service.</summary>
    public string? RebuildOf { get; }

    public bool IsRebuild => RebuildOf is not null;

    public static ProjectionTarget Rebuild(string projection)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projection);
        return new(projection);
    }
}

/// <summary>
/// Transaction des projections : modèles de lecture, événements appliqués et checkpoint du
/// consommateur sont validés ensemble ou pas du tout (ADR 0015). Une session par portée ;
/// la libérer sans <see cref="CommitAsync"/> annule tout.
/// </summary>
public interface IProjectionSession
{
    Task BeginAsync(ProjectionTarget target, CancellationToken cancellationToken);

    Task CommitAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Idempotence et position de chaque projection, dans la transaction de la session.
/// L'idempotence porte sur <c>event_id</c>, pas sur <c>global_sequence</c> : celui-ci croît dans
/// l'ordre d'insertion, pas de validation (ADR 0007), et un événement peut être republié.
/// </summary>
public interface IAppliedEventLog
{
    /// <summary>Marque l'événement comme appliqué à la projection ; faux s'il l'était déjà.</summary>
    Task<bool> TryMarkAppliedAsync(string projection, EventContext context, CancellationToken cancellationToken);

    /// <summary>Avance la position de la projection (plus grande séquence vue, indicateur de fraîcheur).</summary>
    Task AdvanceAsync(string projection, EventContext context, CancellationToken cancellationToken);
}

/// <summary>Position d'un message dans le transport (Kafka) : la prochaine lecture reprend à <c>Offset + 1</c>.</summary>
public sealed record SourcePosition(string ConsumerGroup, string Topic, int Partition, long Offset);

/// <summary>Enregistre la position du consommateur dans la transaction de la session.</summary>
public interface IConsumerCheckpoints
{
    Task SaveAsync(SourcePosition position, CancellationToken cancellationToken);
}

/// <summary>Lit les positions enregistrées, à l'affectation des partitions.</summary>
public interface IConsumerCheckpointReader
{
    /// <summary>Prochain offset à lire, par partition ; une partition absente n'a jamais été lue.</summary>
    Task<IReadOnlyDictionary<int, long>> ReadNextOffsetsAsync(string consumerGroup, string topic, CancellationToken cancellationToken);
}
