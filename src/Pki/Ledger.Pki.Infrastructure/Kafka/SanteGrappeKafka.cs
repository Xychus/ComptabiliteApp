using Confluent.Kafka;
using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Sentinelle;

namespace Ledger.Pki.Infrastructure.Kafka;

/// <summary>
/// Santé de la grappe vue par un client mTLS (certificat du nœud, super-utilisateur) : tous les
/// nœuds attendus présents et aucune partition sous-répliquée.
/// </summary>
public sealed class SanteGrappeKafka(string serveurs, int nombreNoeuds, TimeSpan? delai = null) : ISanteGrappe
{
    private readonly TimeSpan _delai = delai ?? TimeSpan.FromSeconds(20);

    public Task<EtatGrappe> EvaluerAsync(ContenuSecretCertificat identite, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(identite);

        try
        {
            using var admin = new AdminClientBuilder(Configuration(serveurs, identite)).Build();
            return Task.FromResult(Evaluer(admin.GetMetadata(_delai), nombreNoeuds));
        }
        catch (KafkaException ex)
        {
            return Task.FromResult(new EtatGrappe(false, $"grappe injoignable : {ex.Error.Reason}"));
        }
        catch (InvalidOperationException ex)
        {
            // librdkafka refuse la configuration (certificat illisible) avant toute connexion.
            return Task.FromResult(new EtatGrappe(false, $"configuration du client refusée : {ex.Message}"));
        }
    }

    public static AdminClientConfig Configuration(string serveurs, ContenuSecretCertificat identite)
    {
        ArgumentNullException.ThrowIfNull(identite);
        return new AdminClientConfig
        {
            BootstrapServers = serveurs,
            SecurityProtocol = SecurityProtocol.Ssl,
            SslCertificatePem = identite.Chaine,
            SslKeyPem = identite.Cle,
            SslCaPem = identite.Racine,
            SslEndpointIdentificationAlgorithm = SslEndpointIdentificationAlgorithm.Https,
        };
    }

    public static EtatGrappe Evaluer(Metadata metadonnees, int nombreNoeuds)
    {
        ArgumentNullException.ThrowIfNull(metadonnees);

        if (metadonnees.Brokers.Count < nombreNoeuds)
        {
            return new EtatGrappe(false, $"{metadonnees.Brokers.Count} nœud(s) sur {nombreNoeuds}");
        }

        var sousRepliquees = metadonnees.Topics
            .SelectMany(s => s.Partitions.Select(p => (s.Topic, p)))
            .Where(x => x.p.InSyncReplicas.Length < x.p.Replicas.Length)
            .Select(x => $"{x.Topic}-{x.p.PartitionId}")
            .ToList();
        return sousRepliquees.Count == 0
            ? new EtatGrappe(true, $"{metadonnees.Brokers.Count} nœuds, aucune partition sous-répliquée")
            : new EtatGrappe(false, $"{sousRepliquees.Count} partition(s) sous-répliquée(s), dont {sousRepliquees[0]}");
    }
}
