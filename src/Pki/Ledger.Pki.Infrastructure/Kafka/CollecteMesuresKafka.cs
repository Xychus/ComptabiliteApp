using Confluent.Kafka;
using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Sentinelle;

namespace Ledger.Pki.Infrastructure.Kafka;

/// <summary>
/// Collecte par le client d'administration mTLS du nœud : métadonnées (nœuds, ISR), offsets validés
/// des groupes surveillés et offsets de fin des partitions (retard), espace libre du volume de données.
/// Une grappe injoignable (ou un certificat refusé par le client) donne 0 nœud présent : l'alarme se déclenche.
/// </summary>
public sealed class CollecteMesuresKafka(string serveurs, string dossierDonnees, IReadOnlyList<string> groupes, TimeSpan? delai = null) : ICollecteMesuresGrappe
{
    private readonly TimeSpan _delai = delai ?? TimeSpan.FromSeconds(20);

    public Task<MesuresGrappe> CollecterAsync(ContenuSecretCertificat identite, CancellationToken cancellationToken) =>
        Task.FromResult(Collecter(identite));

    private MesuresGrappe Collecter(ContenuSecretCertificat identite)
    {
        var espaceLibre = EspaceLibrePourcent(dossierDonnees);
        try
        {
            using var admin = new AdminClientBuilder(SanteGrappeKafka.Configuration(serveurs, identite)).Build();
            var metadonnees = admin.GetMetadata(_delai);
            var sousRepliquees = metadonnees.Topics.Sum(s => s.Partitions.Count(p => p.InSyncReplicas.Length < p.Replicas.Length));

            var retards = groupes.ToDictionary(g => g, g => Retard(identite, g, metadonnees), StringComparer.Ordinal);
            return new MesuresGrappe(metadonnees.Brokers.Count, sousRepliquees, espaceLibre, retards);
        }
        catch (Exception ex) when (ex is KafkaException or InvalidOperationException)
        {
            return new MesuresGrappe(0, 0, espaceLibre, new Dictionary<string, long>());
        }
    }

    public static double EspaceLibrePourcent(string dossier)
    {
        var lecteur = new DriveInfo(Path.GetFullPath(dossier));
        return lecteur.TotalSize == 0 ? 0 : Math.Round(100.0 * lecteur.AvailableFreeSpace / lecteur.TotalSize, 2);
    }

    // Consommateur du groupe sans abonnement (il ne rejoint pas le groupe) : offsets validés et
    // offsets de fin. Les API d'administration des offsets de librdkafka ont planté le processus
    // (violation d'accès, constaté sous Windows) : ce chemin, plus éprouvé, est préféré.
    private long Retard(ContenuSecretCertificat identite, string groupe, Metadata metadonnees)
    {
        var partitions = metadonnees.Topics
            .Where(s => !s.Topic.StartsWith("__", StringComparison.Ordinal))
            .SelectMany(s => s.Partitions.Select(p => new TopicPartition(s.Topic, p.PartitionId)))
            .ToList();
        if (partitions.Count == 0)
        {
            return 0;
        }

        var configuration = new ConsumerConfig(SanteGrappeKafka.Configuration(serveurs, identite)) { GroupId = groupe, EnableAutoCommit = false };
        using var consommateur = new ConsumerBuilder<Ignore, Ignore>(configuration).Build();
        return consommateur.Committed(partitions, _delai)
            .Where(p => p.Offset.Value >= 0)
            .Sum(p => Math.Max(0, consommateur.QueryWatermarkOffsets(p.TopicPartition, _delai).High.Value - p.Offset.Value));
    }
}
