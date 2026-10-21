using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Events;
using Ledger.Projections.Application.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledger.Projections.Application.Rebuild;

/// <summary>Bilan d'une reconstruction.</summary>
/// <param name="Projection">Projection reconstruite.</param>
/// <param name="Replayed">Événements lus par le curseur sur <c>global_sequence</c>.</param>
/// <param name="CaughtUp">Événements appliqués sous verrou, juste avant la bascule (validés pendant le rejeu, ou hors ordre de séquence).</param>
public sealed record RebuildReport(string Projection, long Replayed, long CaughtUp);

/// <summary>
/// Reconstruit une projection à partir de la table <c>events</c>, sans interrompre le service
/// (ADR 0016) :
/// <list type="number">
/// <item>tables parallèles vides ;</item>
/// <item>rejeu par lots, curseur sur <c>global_sequence</c>, une transaction par lot ;</item>
/// <item>sous verrou exclusif de la projection : application des événements encore absents des
///   tables parallèles (dont ceux validés hors ordre de séquence, ADR 0007), puis bascule.</item>
/// </list>
/// Le consommateur en service continue pendant le rejeu ; après la bascule, l'idempotence par
/// <c>event_id</c> écarte les événements que la reconstruction a déjà appliqués.
/// </summary>
public sealed partial class ProjectionRebuilder(IServiceScopeFactory scopes, IJournalReader journal, EventDecoder decoder, ILogger<ProjectionRebuilder> logger)
{
    public const int DefaultBatchSize = 1000;

    public int BatchSize { get; init; } = DefaultBatchSize;

    public async Task<RebuildReport> RebuildAsync(string projection, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projection);
        if (!ProjectionNames.All.Contains(projection, StringComparer.Ordinal))
        {
            throw new ArgumentException($"Projection inconnue : {projection}.", nameof(projection));
        }

        await InRebuildSessionAsync(projection, (unit, ct) => unit.Rebuild.PrepareAsync(projection, ct), cancellationToken).ConfigureAwait(false);

        long cursor = 0;
        long replayed = 0;
        while (true)
        {
            var batch = await journal.ReadAfterAsync(cursor, BatchSize, cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0)
            {
                break;
            }

            await InRebuildSessionAsync(projection, (unit, ct) => unit.ApplyAsync(batch, ct), cancellationToken).ConfigureAwait(false);
            cursor = batch[^1].GlobalSequence;
            replayed += batch.Count;
            LogBatch(logger, projection, replayed, cursor);
        }

        long caughtUp = 0;
        await InRebuildSessionAsync(
            projection,
            async (unit, ct) =>
            {
                await unit.Rebuild.LockLiveAsync(projection, ct).ConfigureAwait(false);
                var missing = await unit.Rebuild.ReadUnappliedAsync(projection, decoder.EventTypesHandledBy(unit.Projection), ct).ConfigureAwait(false);
                await unit.ApplyAsync(missing, ct).ConfigureAwait(false);
                await unit.Rebuild.SwapAsync(projection, ct).ConfigureAwait(false);
                caughtUp = missing.Count;
            },
            cancellationToken).ConfigureAwait(false);

        LogSwapped(logger, projection, replayed, caughtUp);
        return new RebuildReport(projection, replayed, caughtUp);
    }

    private async Task InRebuildSessionAsync(string projection, Func<RebuildUnit, CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var services = scope.ServiceProvider;
            var session = services.GetRequiredService<IProjectionSession>();
            await session.BeginAsync(ProjectionTarget.Rebuild(projection), cancellationToken).ConfigureAwait(false);
            var unit = new RebuildUnit(
                decoder,
                services.GetRequiredService<ProjectionApplier>(),
                services.GetServices<IProjection>().Single(p => p.Name == projection),
                services.GetRequiredService<IProjectionRebuildStore>());
            await work(unit, cancellationToken).ConfigureAwait(false);
            await session.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Rejeu de {Projection} : {Replayed} événement(s), séquence {Cursor}.")]
    private static partial void LogBatch(ILogger logger, string projection, long replayed, long cursor);

    [LoggerMessage(Level = LogLevel.Information, Message = "Projection {Projection} reconstruite et mise en service : {Replayed} événement(s) rejoué(s), {CaughtUp} rattrapé(s) sous verrou.")]
    private static partial void LogSwapped(ILogger logger, string projection, long replayed, long caughtUp);

    private sealed record RebuildUnit(EventDecoder Decoder, ProjectionApplier Applier, IProjection Projection, IProjectionRebuildStore Rebuild)
    {
        public async Task ApplyAsync(IEnumerable<EventEnvelopeV1> events, CancellationToken cancellationToken)
        {
            foreach (var envelope in events)
            {
                await Applier.ApplyAsync(Decoder.Decode(envelope), [Projection], cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
