using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Ledger.Pki.Application.Inventaire;

namespace Ledger.Pki.Infrastructure.Aws;

/// <summary>Publie les échéances en métrique CloudWatch <c>Ledger/Pki</c> / <c>JoursAvantEcheance</c>, dimension <c>Identite</c>.</summary>
public sealed class PublicateurMesuresCloudWatch(IAmazonCloudWatch cloudWatch) : IPublicateurMesures
{
    public const string EspaceDeNoms = "Ledger/Pki";
    public const string NomMetrique = "JoursAvantEcheance";
    public const string Dimension = "Identite";

    // Limite de PutMetricData : 1 000 mesures par appel ; découpage prudent.
    private const int TailleLot = 500;

    public async Task PublierAsync(IReadOnlyList<MesureEcheance> mesures, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mesures);

        foreach (var lot in mesures.Chunk(TailleLot))
        {
            await cloudWatch.PutMetricDataAsync(
                new PutMetricDataRequest
                {
                    Namespace = EspaceDeNoms,
                    MetricData = [.. lot.Select(m => new MetricDatum
                    {
                        MetricName = NomMetrique,
                        Dimensions = [new Dimension { Name = Dimension, Value = m.Identite }],
                        Value = m.JoursRestants,
                        Unit = StandardUnit.None,
                    })],
                },
                cancellationToken).ConfigureAwait(false);
        }
    }
}
