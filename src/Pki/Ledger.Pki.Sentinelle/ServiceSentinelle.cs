using Ledger.Pki.Application.Sentinelle;

namespace Ledger.Pki.Sentinelle;

/// <summary>
/// Boucle de surveillance : à chaque intervalle, demande un verdict à la <see cref="Application.Sentinelle.Sentinelle"/> ;
/// sur « redémarrer », arrête l'hôte avec le code <see cref="CodeRedemarrage"/>. Le conteneur est
/// essentiel : ECS arrête la tâche et le service en relance une, qui installe le nouveau certificat.
/// </summary>
public sealed partial class ServiceSentinelle(
    Application.Sentinelle.Sentinelle sentinelle,
    OptionsSentinelle options,
    IHostApplicationLifetime cycleDeVie,
    TimeProvider temps,
    ILogger<ServiceSentinelle> journal) : BackgroundService
{
    public const int CodeRedemarrage = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(options.Intervalle, temps, stoppingToken).ConfigureAwait(false);

            VerdictSentinelle verdict;
            try
            {
                verdict = await sentinelle.VerifierAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Une panne passagère (Secrets Manager, Kafka) ne doit pas redémarrer le nœud.
                JournaliserEchec(ex);
                continue;
            }

            JournaliserVerdict(verdict.Decision, verdict.Raison);
            if (verdict.Decision == DecisionSentinelle.Redemarrer)
            {
                Environment.ExitCode = CodeRedemarrage;
                cycleDeVie.StopApplication();
                return;
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Sentinelle : {Decision} — {Raison}")]
    private partial void JournaliserVerdict(DecisionSentinelle decision, string raison);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Sentinelle : vérification impossible, nouvelle tentative au prochain intervalle.")]
    private partial void JournaliserEchec(Exception exception);
}
