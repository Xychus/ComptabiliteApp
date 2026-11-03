using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Ledger.Pki.Application.Sentinelle;

namespace Ledger.Pki.Infrastructure.Aws;

/// <summary>
/// Métriques <c>Ledger/Kafka</c> : <c>NoeudsPresents</c> et <c>PartitionsSousRepliquees</c> (par nœud
/// observateur), <c>EspaceDisqueLibrePourcent</c> (par nœud), <c>RetardGroupe</c> (par groupe).
/// </summary>
public sealed class PublicateurMesuresGrappeCloudWatch(IAmazonCloudWatch cloudWatch) : IPublicateurMesuresGrappe
{
    public const string EspaceDeNoms = "Ledger/Kafka";

    public Task PublierAsync(string noeud, MesuresGrappe mesures, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mesures);

        List<MetricDatum> donnees =
        [
            Mesure("NoeudsPresents", mesures.NoeudsPresents, StandardUnit.Count, ("Noeud", noeud)),
            Mesure("PartitionsSousRepliquees", mesures.PartitionsSousRepliquees, StandardUnit.Count, ("Noeud", noeud)),
            Mesure("EspaceDisqueLibrePourcent", mesures.EspaceLibrePourcent, StandardUnit.Percent, ("Noeud", noeud)),
            .. mesures.RetardsGroupes.Select(r => Mesure("RetardGroupe", r.Value, StandardUnit.Count, ("Groupe", r.Key))),
        ];
        return cloudWatch.PutMetricDataAsync(new PutMetricDataRequest { Namespace = EspaceDeNoms, MetricData = donnees }, cancellationToken);
    }

    private static MetricDatum Mesure(string nom, double valeur, StandardUnit unite, (string Nom, string Valeur) dimension) => new()
    {
        MetricName = nom,
        Value = valeur,
        Unit = unite,
        Dimensions = [new Dimension { Name = dimension.Nom, Value = dimension.Valeur }],
    };
}
