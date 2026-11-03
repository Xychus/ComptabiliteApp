using Ledger.Pki.Application.Emission;

namespace Ledger.Pki.Application.Sentinelle;

/// <summary>
/// Mesures de la grappe vues par un nœud : nœuds présents, partitions sous-répliquées, espace libre
/// du volume de données du nœud, retard (messages non consommés) de chaque groupe surveillé.
/// </summary>
public sealed record MesuresGrappe(
    int NoeudsPresents,
    int PartitionsSousRepliquees,
    double EspaceLibrePourcent,
    IReadOnlyDictionary<string, long> RetardsGroupes);

/// <summary>Port de collecte des mesures (client Kafka mTLS du nœud, système de fichiers).</summary>
public interface ICollecteMesuresGrappe
{
    Task<MesuresGrappe> CollecterAsync(ContenuSecretCertificat identite, CancellationToken cancellationToken);
}

/// <summary>Port de publication (métriques CloudWatch <c>Ledger/Kafka</c> en production).</summary>
public interface IPublicateurMesuresGrappe
{
    Task PublierAsync(string noeud, MesuresGrappe mesures, CancellationToken cancellationToken);
}

/// <summary>
/// Collecte et publie les mesures de la grappe (alarmes de la phase 8 : sous-réplication, disque,
/// retard des groupes). Chaque nœud publie ses mesures ; l'alarme retient le pire.
/// </summary>
public sealed class SurveillanceGrappe(
    ISourceCertificatCourant source,
    ICollecteMesuresGrappe collecte,
    IPublicateurMesuresGrappe publicateur,
    string noeud)
{
    public async Task<MesuresGrappe> ExecuterAsync(CancellationToken cancellationToken)
    {
        var identite = await source.LireAsync(cancellationToken).ConfigureAwait(false);
        var mesures = await collecte.CollecterAsync(identite.Contenu, cancellationToken).ConfigureAwait(false);
        await publicateur.PublierAsync(noeud, mesures, cancellationToken).ConfigureAwait(false);
        return mesures;
    }
}
