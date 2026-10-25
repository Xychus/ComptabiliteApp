using System.Net;
using System.Net.Sockets;
using System.Text;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Profils;
using Ledger.Pki.Application.Sentinelle;
using Ledger.Pki.Infrastructure.Fichiers;
using Ledger.Pki.Infrastructure.Kafka;
using Ledger.Pki.Tests.Support;

namespace Ledger.Pki.Infrastructure.Tests.Sentinelle;

/// <summary>
/// Broker <c>apache/kafka</c> réel (même image qu'en local et sur ECS) configuré comme en AWS
/// (ADR 0024) : écouteurs SSL seulement (client et contrôleur), mTLS exigé, TLS 1.3, keystore et
/// truststore PEM écrits par la sentinelle avec un certificat de la PKI interne, refus par défaut.
/// L'écouteur s'appelle <c>MTLS</c>, pas <c>SSL</c> : sur un écouteur nommé <c>SSL://</c>, le script
/// de l'image exige des keystores JKS protégés par mot de passe.
/// </summary>
[Trait("Category", "Integration")]
public sealed class KafkaMtlsTests : IAsyncLifetime
{
    public const string Image = "apache/kafka:4.3.2";

    // Heure réelle : le broker vérifie les dates des certificats.
    private readonly AutoriteEssai _pki = new(maintenant: DateTimeOffset.UtcNow);
    private readonly string _dossier = Path.Join(Path.GetTempPath(), $"kafka-mtls-{Guid.NewGuid():N}");
    private readonly int _port = PortLibre();
    private IContainer? _kafka;
    private ContenuSecretCertificat _broker = null!;

    private string Serveurs => $"localhost:{_port}";

    public async ValueTask InitializeAsync()
    {
        _broker = (await _pki.Emetteur().EmettreAsync(AdaptateursSentinelleTests.ProfilBroker, TestContext.Current.CancellationToken)).Value;
        await new EcrivainCertificatsFichiers(_dossier).EcrireAsync(new CertificatCourant("v1", _broker), TestContext.Current.CancellationToken);

        _kafka = new ContainerBuilder(Image)
            .WithPortBinding(_port, _port)
            .WithResourceMapping(Encoding.ASCII.GetBytes(await File.ReadAllTextAsync(Path.Join(_dossier, EcrivainCertificatsFichiers.Keystore))), "/certificats/keystore.pem")
            .WithResourceMapping(Encoding.ASCII.GetBytes(await File.ReadAllTextAsync(Path.Join(_dossier, EcrivainCertificatsFichiers.Truststore))), "/certificats/truststore.pem")
            .WithEnvironment(new Dictionary<string, string>
            {
                ["CLUSTER_ID"] = "bGVkZ2VyLW10bHMtMDAx",
                ["KAFKA_NODE_ID"] = "1",
                ["KAFKA_PROCESS_ROLES"] = "broker,controller",
                ["KAFKA_CONTROLLER_QUORUM_VOTERS"] = "1@localhost:9094",
                ["KAFKA_LISTENERS"] = $"MTLS://0.0.0.0:{_port},CONTROLLER://0.0.0.0:9094",
                ["KAFKA_ADVERTISED_LISTENERS"] = $"MTLS://localhost:{_port}",
                ["KAFKA_LISTENER_SECURITY_PROTOCOL_MAP"] = "MTLS:SSL,CONTROLLER:SSL",
                ["KAFKA_INTER_BROKER_LISTENER_NAME"] = "MTLS",
                ["KAFKA_CONTROLLER_LISTENER_NAMES"] = "CONTROLLER",
                ["KAFKA_SSL_KEYSTORE_TYPE"] = "PEM",
                ["KAFKA_SSL_KEYSTORE_LOCATION"] = "/certificats/keystore.pem",
                ["KAFKA_SSL_TRUSTSTORE_TYPE"] = "PEM",
                ["KAFKA_SSL_TRUSTSTORE_LOCATION"] = "/certificats/truststore.pem",
                ["KAFKA_SSL_CLIENT_AUTH"] = "required",
                ["KAFKA_SSL_PROTOCOL"] = "TLSv1.3",
                ["KAFKA_SSL_ENABLED_PROTOCOLS"] = "TLSv1.3",
                ["KAFKA_SSL_PRINCIPAL_MAPPING_RULES"] = "RULE:^CN=([a-z0-9-]+)$/$1/,DEFAULT",
                ["KAFKA_AUTHORIZER_CLASS_NAME"] = "org.apache.kafka.metadata.authorizer.StandardAuthorizer",
                ["KAFKA_SUPER_USERS"] = "User:kafka-1",
                ["KAFKA_ALLOW_EVERYONE_IF_NO_ACL_FOUND"] = "false",
                ["KAFKA_AUTO_CREATE_TOPICS_ENABLE"] = "false",
                ["KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR"] = "1",
                ["KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR"] = "1",
                ["KAFKA_TRANSACTION_STATE_LOG_MIN_ISR"] = "1",
            })
            .WithWaitStrategy(Wait.ForUnixContainer().UntilMessageIsLogged("Kafka Server started"))
            .Build();
        try
        {
            await _kafka.StartAsync(TestContext.Current.CancellationToken);
        }
        catch (Exception ex) when (ex is ContainerNotRunningException or TimeoutException)
        {
            var (sortie, erreurs) = await _kafka.GetLogsAsync(ct: TestContext.Current.CancellationToken);
            throw new InvalidOperationException($"Le broker n'a pas démarré :{Environment.NewLine}{sortie}{Environment.NewLine}{erreurs}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_kafka is not null)
        {
            await _kafka.DisposeAsync();
        }

        _pki.Dispose();
        if (Directory.Exists(_dossier))
        {
            Directory.Delete(_dossier, recursive: true);
        }
    }

    [Fact]
    public async Task Broker_accepte_son_certificat_pem_et_la_grappe_est_saine()
    {
        var etat = await new SanteGrappeKafka(Serveurs, 1).EvaluerAsync(_broker, TestContext.Current.CancellationToken);

        Assert.True(etat.Saine, etat.Detail);
    }

    [Fact]
    public async Task Client_d_une_autre_pki_est_refuse()
    {
        using var autrePki = new AutoriteEssai(nomRacine: "CN=Racine etrangere", maintenant: DateTimeOffset.UtcNow);
        var etranger = (await autrePki.Emetteur().EmettreAsync(new ProfilCertificat("debezium", [], UsageCertificat.Client), TestContext.Current.CancellationToken)).Value;

        var etat = await new SanteGrappeKafka(Serveurs, 1, TimeSpan.FromSeconds(10))
            .EvaluerAsync(etranger with { Racine = _broker.Racine }, TestContext.Current.CancellationToken);

        Assert.False(etat.Saine);
    }

    [Fact]
    public async Task Client_sans_acl_ne_peut_rien_creer()
    {
        var intrus = (await _pki.Emetteur().EmettreAsync(new ProfilCertificat("intrus", [], UsageCertificat.Client), TestContext.Current.CancellationToken)).Value;
        using var admin = new AdminClientBuilder(SanteGrappeKafka.Configuration(Serveurs, intrus)).Build();

        var erreur = await Assert.ThrowsAsync<CreateTopicsException>(() =>
            admin.CreateTopicsAsync([new TopicSpecification { Name = "intrusion", NumPartitions = 1, ReplicationFactor = 1 }]));

        Assert.Contains(erreur.Results, r => r.Error.Code is ErrorCode.TopicAuthorizationFailed or ErrorCode.ClusterAuthorizationFailed);
    }

    private static int PortLibre()
    {
        using var ecoute = new TcpListener(IPAddress.Loopback, 0);
        ecoute.Start();
        return ((IPEndPoint)ecoute.LocalEndpoint).Port;
    }
}
