using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ledger.Projections.Application.Processing;

/// <summary>
/// Source des événements publiés (Kafka). <see cref="Consume"/> bloque jusqu'au message suivant ;
/// l'ordre est conservé par partition (clé = entité, ADR 0007).
/// </summary>
public interface IEventSource
{
    SourceMessage Consume(CancellationToken cancellationToken);

    /// <summary>Signale au transport que le message est traité (suivi du retard ; la reprise vient du checkpoint en base).</summary>
    void Acknowledge(SourceMessage message);
}

/// <summary>
/// Boucle du worker : un message à la fois, chacun dans sa propre portée et sa propre transaction.
/// Une erreur arrête la boucle : le worker redémarre et reprend au dernier checkpoint validé,
/// sans perte ni double application (ADR 0015).
/// </summary>
public sealed partial class ProjectionPump(IServiceScopeFactory scopes, ILogger<ProjectionPump> logger)
{
    public async Task RunAsync(IEventSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        while (!cancellationToken.IsCancellationRequested)
        {
            SourceMessage message;
            try
            {
                message = source.Consume(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await ProcessAsync(message, cancellationToken).ConfigureAwait(false);
            source.Acknowledge(message);
        }
    }

    public async Task ProcessAsync(SourceMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var scope = scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            await scope.ServiceProvider.GetRequiredService<ProjectionMessageProcessor>().ProcessAsync(message, cancellationToken).ConfigureAwait(false);
        }

        LogApplied(logger, message.Envelope.GlobalSequence, message.Envelope.EventType, message.Position.Partition, message.Position.Offset);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Événement {GlobalSequence} ({EventType}) projeté depuis la partition {Partition}, offset {Offset}.")]
    private static partial void LogApplied(ILogger logger, long globalSequence, string eventType, int partition, long offset);
}
