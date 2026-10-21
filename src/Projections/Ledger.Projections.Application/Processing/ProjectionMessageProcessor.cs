using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Events;

namespace Ledger.Projections.Application.Processing;

/// <summary>Message reçu du transport : enveloppe publiée par l'outbox et sa position.</summary>
public sealed record SourceMessage(EventEnvelopeV1 Envelope, SourcePosition Position);

/// <summary>
/// Traite un message dans une transaction : toutes les projections en service, puis le
/// checkpoint du consommateur (ADR 0015). Une instance par message (portée).
/// </summary>
public sealed class ProjectionMessageProcessor(
    EventDecoder decoder,
    IProjectionSession session,
    ProjectionApplier applier,
    IEnumerable<IProjection> projections,
    IConsumerCheckpoints checkpoints)
{
    public async Task ProcessAsync(SourceMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var decodedEvent = decoder.Decode(message.Envelope);
        await session.BeginAsync(ProjectionTarget.Live, cancellationToken).ConfigureAwait(false);
        await applier.ApplyAsync(decodedEvent, projections, cancellationToken).ConfigureAwait(false);
        await checkpoints.SaveAsync(message.Position, cancellationToken).ConfigureAwait(false);
        await session.CommitAsync(cancellationToken).ConfigureAwait(false);
    }
}
