using Ledger.Projections.Application.Events;

namespace Ledger.Projections.Application.Processing;

/// <summary>
/// Applique un événement décodé à des projections, dans la session courante. Chaque projection
/// est indépendante : elle marque l'événement pour elle-même, puis le traite seulement s'il ne
/// l'était pas déjà (même événement livré deux fois → effet unique).
/// </summary>
public sealed class ProjectionApplier(IAppliedEventLog appliedEvents)
{
    public async Task ApplyAsync(DecodedEvent decodedEvent, IEnumerable<IProjection> projections, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(decodedEvent);
        ArgumentNullException.ThrowIfNull(projections);
        foreach (var projection in projections)
        {
            if (decodedEvent.IsHandledBy(projection)
                && await appliedEvents.TryMarkAppliedAsync(projection.Name, decodedEvent.Context, cancellationToken).ConfigureAwait(false))
            {
                await decodedEvent.DispatchToAsync(projection, cancellationToken).ConfigureAwait(false);
            }

            await appliedEvents.AdvanceAsync(projection.Name, decodedEvent.Context, cancellationToken).ConfigureAwait(false);
        }
    }
}
