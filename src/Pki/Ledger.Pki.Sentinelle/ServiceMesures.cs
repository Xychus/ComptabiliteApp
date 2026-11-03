using Ledger.Pki.Application.Sentinelle;

namespace Ledger.Pki.Sentinelle;

/// <summary>Publication périodique des mesures de la grappe ; une erreur passagère n'arrête pas la sentinelle.</summary>
public sealed partial class ServiceMesures(
    SurveillanceGrappe surveillance,
    ConfigurationSentinelle configuration,
    TimeProvider temps,
    ILogger<ServiceMesures> journal) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var mesures = await surveillance.ExecuterAsync(stoppingToken).ConfigureAwait(false);
                JournaliserMesures(mesures.NoeudsPresents, mesures.PartitionsSousRepliquees, mesures.EspaceLibrePourcent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                JournaliserEchec(ex);
            }

            await Task.Delay(configuration.IntervalleMesures, temps, stoppingToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Mesures : {Noeuds} nœud(s), {SousRepliquees} partition(s) sous-répliquée(s), {EspaceLibre} % libre")]
    private partial void JournaliserMesures(int noeuds, int sousRepliquees, double espaceLibre);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Mesures : publication impossible, nouvelle tentative au prochain intervalle.")]
    private partial void JournaliserEchec(Exception exception);
}
