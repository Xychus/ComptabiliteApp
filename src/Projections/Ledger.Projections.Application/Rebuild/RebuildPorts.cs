using Ledger.Contracts.Events.V1;

namespace Ledger.Projections.Application.Rebuild;

/// <summary>Lecture de la table <c>events</c>, seule source de vérité : rejeu et instantanés de période.</summary>
public interface IJournalReader
{
    /// <summary>Au plus <paramref name="maxCount"/> événements de séquence supérieure à <paramref name="afterSequence"/>, dans l'ordre de la séquence.</summary>
    Task<IReadOnlyList<EventEnvelopeV1>> ReadAfterAsync(long afterSequence, int maxCount, CancellationToken cancellationToken);

    /// <summary>Événements des types <paramref name="eventTypes"/> d'une entité dans une période, dans l'ordre de la séquence.</summary>
    Task<IReadOnlyList<EventEnvelopeV1>> ReadPeriodEventsAsync(
        string entityId,
        string period,
        IReadOnlyCollection<string> eventTypes,
        CancellationToken cancellationToken);
}

/// <summary>
/// Tables parallèles d'une reconstruction (ADR 0016), dans la transaction de la session ouverte
/// sur <see cref="Processing.ProjectionTarget.Rebuild"/>.
/// </summary>
public interface IProjectionRebuildStore
{
    /// <summary>Crée des tables parallèles vides (une reconstruction précédente inachevée est abandonnée).</summary>
    Task PrepareAsync(string projection, CancellationToken cancellationToken);

    /// <summary>
    /// Verrou exclusif sur la projection jusqu'à la fin de la transaction : le consommateur en
    /// service attend, puis reprend sur les nouvelles tables.
    /// </summary>
    Task LockLiveAsync(string projection, CancellationToken cancellationToken);

    /// <summary>Événements des types donnés que les tables parallèles n'ont pas encore appliqués, dans l'ordre de la séquence.</summary>
    Task<IReadOnlyList<EventEnvelopeV1>> ReadUnappliedAsync(string projection, IReadOnlyCollection<string> eventTypes, CancellationToken cancellationToken);

    /// <summary>Met les tables parallèles en service et supprime les anciennes.</summary>
    Task SwapAsync(string projection, CancellationToken cancellationToken);
}
