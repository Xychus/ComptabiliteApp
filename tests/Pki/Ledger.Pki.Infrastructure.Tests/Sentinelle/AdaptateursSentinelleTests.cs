using Confluent.Kafka;
using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Profils;
using Ledger.Pki.Application.Sentinelle;
using Ledger.Pki.Infrastructure.Aws;
using Ledger.Pki.Infrastructure.Fichiers;
using Ledger.Pki.Infrastructure.Kafka;
using Ledger.Pki.Infrastructure.Memoire;
using Ledger.Pki.Infrastructure.Tests.Contrats;
using Ledger.Pki.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Pki.Infrastructure.Tests.Sentinelle;

public sealed class AdaptateursSentinelleTests : IDisposable
{
    private readonly string _dossier = Path.Join(Path.GetTempPath(), $"sentinelle-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_dossier))
        {
            Directory.Delete(_dossier, recursive: true);
        }
    }

    private static CancellationToken Jeton => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EcrivainCertificatsFichiers_ecrit_keystore_truststore_et_version()
    {
        var ecrivain = new EcrivainCertificatsFichiers(_dossier);
        var contenu = new ContenuSecretCertificat("CERT", "CERT\nINTER\n", "CLE\n", "RACINE", AutoriteEssai.Maintenant.AddDays(7));

        Assert.Null(await ecrivain.LireInstalleAsync(Jeton));
        await ecrivain.EcrireAsync(new CertificatCourant("v1", contenu), Jeton);

        Assert.Equal("CLE\nCERT\nINTER\n", await File.ReadAllTextAsync(Path.Join(_dossier, EcrivainCertificatsFichiers.Keystore), Jeton));
        Assert.Equal("RACINE\n", await File.ReadAllTextAsync(Path.Join(_dossier, EcrivainCertificatsFichiers.Truststore), Jeton));
        Assert.Equal(new CertificatInstalle("v1", AutoriteEssai.Maintenant.AddDays(7)), await ecrivain.LireInstalleAsync(Jeton));
        Assert.Empty(Directory.GetFiles(_dossier, "*.tmp"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Join(_dossier, EcrivainCertificatsFichiers.Keystore)));
        }
    }

    [Fact]
    public async Task EcrivainCertificatsFichiers_version_illisible_est_ignoree()
    {
        Directory.CreateDirectory(_dossier);
        await File.WriteAllTextAsync(Path.Join(_dossier, EcrivainCertificatsFichiers.Version), "v1\npas une date\n", Jeton);

        Assert.Null(await new EcrivainCertificatsFichiers(_dossier).LireInstalleAsync(Jeton));
    }

    [Fact]
    public async Task SourceCertificatSecretsManager_lit_version_et_contenu_courants()
    {
        var memoire = new MagasinCertificatsMemoire();
        memoire.Creer("s", new Dictionary<string, string>(StringComparer.Ordinal));
        memoire.AmorcerRotation("s", "v1");
        var contenu = new ContenuSecretCertificat("c", "c", "k", "r", AutoriteEssai.Maintenant);
        await memoire.EcrireEnAttenteAsync("s", "v1", contenu.VersJson(), Jeton);
        await memoire.PromouvoirAsync("s", "v1", null, Jeton);
        using var client = new SecretsManagerEssai(memoire);
        var source = new SourceCertificatSecretsManager(client, "s");

        Assert.Equal(new CertificatCourant("v1", contenu), await source.LireAsync(Jeton));
        Assert.Equal("v1", await source.LireVersionAsync(Jeton));
    }

    [Fact]
    public async Task SourceCertificatSecretsManager_secret_sans_version_courante_ou_illisible_echoue()
    {
        var memoire = new MagasinCertificatsMemoire();
        memoire.Creer("s", new Dictionary<string, string>(StringComparer.Ordinal));
        using var client = new SecretsManagerEssai(memoire);
        var source = new SourceCertificatSecretsManager(client, "s");

        await Assert.ThrowsAsync<InvalidOperationException>(() => source.LireVersionAsync(Jeton));

        memoire.AmorcerRotation("s", "v1");
        await memoire.EcrireEnAttenteAsync("s", "v1", "pas du json", Jeton);
        await memoire.PromouvoirAsync("s", "v1", null, Jeton);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.LireAsync(Jeton));
    }

    [Fact]
    public void SanteGrappeKafka_evalue_noeuds_et_partitions()
    {
        var brokers = Enumerable.Range(1, 3).Select(i => new BrokerMetadata(i, $"kafka-{i}", 9093)).ToList();
        var saine = new Metadata(brokers, [new TopicMetadata("ledger.events", [new PartitionMetadata(0, 1, [1, 2, 3], [1, 2, 3], new Error(ErrorCode.NoError))], new Error(ErrorCode.NoError))], 1, "kafka-1");
        var sousRepliquee = new Metadata(brokers, [new TopicMetadata("ledger.events", [new PartitionMetadata(4, 1, [1, 2, 3], [1, 2], new Error(ErrorCode.NoError))], new Error(ErrorCode.NoError))], 1, "kafka-1");
        var incomplete = new Metadata(brokers.Take(2).ToList(), [], 1, "kafka-1");

        Assert.True(SanteGrappeKafka.Evaluer(saine, 3).Saine);
        Assert.Equal(new EtatGrappe(false, "1 partition(s) sous-répliquée(s), dont ledger.events-4"), SanteGrappeKafka.Evaluer(sousRepliquee, 3));
        Assert.Equal(new EtatGrappe(false, "2 nœud(s) sur 3"), SanteGrappeKafka.Evaluer(incomplete, 3));
    }

    [Fact]
    public async Task SanteGrappeKafka_grappe_injoignable_n_est_pas_saine()
    {
        var etat = await new SanteGrappeKafka("127.0.0.1:1", 3, TimeSpan.FromSeconds(1))
            .EvaluerAsync(new ContenuSecretCertificat("c", "c", "k", "r", AutoriteEssai.Maintenant), Jeton);

        Assert.False(etat.Saine);
    }

    [Fact]
    public void AjouterSentinelleAws_enregistre_les_ports()
    {
        var services = new ServiceCollection().AjouterSentinelleAws("arn:secret", _dossier, "kafka-1:9093", 3);

        Assert.Contains(services, s => s.ServiceType == typeof(ISourceCertificatCourant));
        Assert.Contains(services, s => s.ServiceType == typeof(IEcrivainCertificats));
        Assert.Contains(services, s => s.ServiceType == typeof(ISanteGrappe));
    }

    [Fact]
    public void Configuration_admin_en_mtls_avec_verification_du_nom()
    {
        var config = SanteGrappeKafka.Configuration("kafka-1:9093", new ContenuSecretCertificat("c", "chaine", "cle", "racine", AutoriteEssai.Maintenant));

        Assert.Equal(SecurityProtocol.Ssl, config.SecurityProtocol);
        Assert.Equal(("chaine", "cle", "racine"), (config.SslCertificatePem, config.SslKeyPem, config.SslCaPem));
        Assert.Equal(SslEndpointIdentificationAlgorithm.Https, config.SslEndpointIdentificationAlgorithm);
    }

    internal static ProfilCertificat ProfilBroker => new("kafka-1", ["localhost"], UsageCertificat.Serveur | UsageCertificat.Client);
}
