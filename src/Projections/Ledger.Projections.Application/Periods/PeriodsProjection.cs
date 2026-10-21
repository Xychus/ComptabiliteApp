using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Events;
using Ledger.Projections.Application.Processing;

namespace Ledger.Projections.Application.Periods;

/// <summary>Ouverture d'une période ; <see cref="Version"/> est la version de son flux.</summary>
public sealed record PeriodOpening(string EntityId, string Period, long Version, DateTimeOffset OpenedAt);

/// <summary>Scellement d'une période : empreinte des événements scellés (ADR 0013).</summary>
public sealed record PeriodSeal(string EntityId, string Period, long Version, DateTimeOffset SealedAt, long EventCount, string? MerkleRoot);

/// <summary>Port d'écriture de <c>periods</c>.</summary>
public interface IPeriodStatusStore
{
    Task OpenAsync(PeriodOpening opening, CancellationToken cancellationToken);

    Task SealAsync(PeriodSeal seal, CancellationToken cancellationToken);
}

/// <summary>Statut des périodes (<c>Open</c>, <c>Sealed</c>) et empreinte de leur sceau.</summary>
public sealed class PeriodsProjection(IPeriodStatusStore store)
    : IProjection, IEventHandler<PeriodOpenedV1>, IEventHandler<PeriodSealedV1>
{
    public const string Open = "Open";
    public const string Sealed = "Sealed";

    public string Name => ProjectionNames.Periods;

    public Task HandleAsync(ProjectedEvent<PeriodOpenedV1> projectedEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectedEvent);
        var context = projectedEvent.Context;
        return store.OpenAsync(new PeriodOpening(projectedEvent.Data.EntityId, projectedEvent.Data.Period, context.StreamVersion, context.RecordedAt), cancellationToken);
    }

    public Task HandleAsync(ProjectedEvent<PeriodSealedV1> projectedEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectedEvent);
        var data = projectedEvent.Data;
        var context = projectedEvent.Context;
        return store.SealAsync(new PeriodSeal(data.EntityId, data.Period, context.StreamVersion, context.RecordedAt, data.EventCount, data.MerkleRoot), cancellationToken);
    }
}
