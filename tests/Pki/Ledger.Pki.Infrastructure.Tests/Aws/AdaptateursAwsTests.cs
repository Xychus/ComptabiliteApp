using System.Security.Cryptography;
using Amazon;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Amazon.Runtime;
using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Inventaire;
using Ledger.Pki.Application.Profils;
using Ledger.Pki.Application.Rotation;
using Ledger.Pki.Infrastructure.Aws;
using Ledger.Pki.Infrastructure.Memoire;
using Ledger.Pki.Infrastructure.Tests.Contrats;
using Ledger.Pki.Tests.Support;
using Task = System.Threading.Tasks.Task;

namespace Ledger.Pki.Infrastructure.Tests.Aws;

public sealed class AdaptateursAwsTests : IDisposable
{
    private static readonly AnonymousAWSCredentials _anonyme = new();

    private readonly AutoriteEssai _pki = new();

    public void Dispose() => _pki.Dispose();

    private static CancellationToken Jeton => TestContext.Current.CancellationToken;

    private static DescriptionSecret Description(params (string Cle, string Valeur)[] etiquettes) =>
        new("arn:secret", true, new Dictionary<string, IReadOnlyList<string>>(), etiquettes.ToDictionary(e => e.Cle, e => e.Valeur, StringComparer.Ordinal));

    [Fact]
    public async Task SignataireKms_signe_un_condense_sha384_verifiable_par_la_cle_publique()
    {
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var kms = new KmsEssai(cle);
        var condense = SHA384.HashData("tbs"u8);

        var signature = await new SignataireKms(kms).SignerCondenseAsync("arn:cle", condense, Jeton);
        var clePublique = await new SignataireKms(kms).LireClePubliqueAsync("arn:cle", Jeton);

        Assert.Equal(MessageType.DIGEST, kms.Requete!.MessageType);
        Assert.Equal(SigningAlgorithmSpec.ECDSA_SHA_384, kms.Requete.SigningAlgorithm);
        Assert.Equal("arn:cle", kms.Requete.KeyId);
        using var verificateur = ECDsa.Create();
        verificateur.ImportSubjectPublicKeyInfo(clePublique, out _);
        Assert.True(verificateur.VerifyHash(condense, signature, DSASignatureFormat.Rfc3279DerSequence));
    }

    [Fact]
    public async Task SourceInventaireSecretsManager_parcourt_toutes_les_pages()
    {
        var memoire = new MagasinCertificatsMemoire();
        using var client = new SecretsManagerEssai(memoire, taillePage: 1);
        foreach (var nom in new[] { "a", "b", "c" })
        {
            memoire.Creer(nom, new Dictionary<string, string>(StringComparer.Ordinal) { [ProfilCertificat.EtiquetteNomCommun] = $"cn-{nom}" });
            client.Secrets.Add(nom);
        }

        memoire.AmorcerRotation("b", "v1");
        await memoire.EcrireEnAttenteAsync("b", "v1", "valeur-b", Jeton);
        await memoire.PromouvoirAsync("b", "v1", null, Jeton);

        var recenses = await new SourceInventaireSecretsManager(client).ListerAsync(Jeton);

        Assert.Equal([new SecretRecense("cn-a", null), new SecretRecense("cn-b", "valeur-b"), new SecretRecense("cn-c", null)], recenses);
    }

    [Fact]
    public async Task SourceInventaireSecretsManager_aucun_secret()
    {
        using var client = new SecretsManagerEssai(new MagasinCertificatsMemoire());

        Assert.Empty(await new SourceInventaireSecretsManager(client).ListerAsync(Jeton));
    }

    [Fact]
    public async Task PublicateurMesuresCloudWatch_publie_par_identite()
    {
        using var cloudWatch = new CloudWatchEssai();
        var mesures = Enumerable.Range(0, 501).Select(i => new MesureEcheance($"id-{i}", i)).ToList();

        await new PublicateurMesuresCloudWatch(cloudWatch).PublierAsync(mesures, Jeton);

        Assert.Equal([500, 1], cloudWatch.Requetes.Select(r => r.MetricData.Count));
        var premiere = cloudWatch.Requetes[0];
        Assert.Equal("Ledger/Pki", premiere.Namespace);
        Assert.Equal("JoursAvantEcheance", premiere.MetricData[0].MetricName);
        Assert.Equal(("Identite", "id-0"), (premiere.MetricData[0].Dimensions[0].Name, premiere.MetricData[0].Dimensions[0].Value));
    }

    [Fact]
    public async Task RedeploiementEcs_force_un_deploiement_par_service()
    {
        using var ecs = new EcsEssai();
        var action = new RedeploiementEcs(ecs);
        var description = Description((RedeploiementEcs.EtiquetteGrappe, "ledger-dev"), (RedeploiementEcs.EtiquetteServices, "debezium  projections-worker"));

        Assert.True(action.SApplique(description));
        await action.ExecuterAsync(description, "{}", Jeton);

        Assert.Equal([("ledger-dev", "debezium", true), ("ledger-dev", "projections-worker", true)], ecs.Requetes.Select(r => (r.Cluster, r.Service, r.ForceNewDeployment ?? false)));
    }

    [Theory]
    [InlineData(null, "debezium")]
    [InlineData("ledger-dev", null)]
    [InlineData("ledger-dev", " ")]
    public void RedeploiementEcs_ne_s_applique_pas_sans_grappe_ni_service(string? grappe, string? services)
    {
        using var ecs = new EcsEssai();
        var etiquettes = new List<(string, string)>();
        if (grappe is not null)
        {
            etiquettes.Add((RedeploiementEcs.EtiquetteGrappe, grappe));
        }

        if (services is not null)
        {
            etiquettes.Add((RedeploiementEcs.EtiquetteServices, services));
        }

        Assert.False(new RedeploiementEcs(ecs).SApplique(Description([.. etiquettes])));
    }

    [Fact]
    public void AjouterPkiAws_enregistre_tous_les_ports()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection().AjouterPkiAws();

        Assert.Contains(services, s => s.ServiceType == typeof(ISignataireAutorite) && s.ImplementationType == typeof(SignataireKms));
        Assert.Contains(services, s => s.ServiceType == typeof(IMagasinCertificats));
        Assert.Contains(services, s => s.ServiceType == typeof(ISourceInventaire));
        Assert.Contains(services, s => s.ServiceType == typeof(IPublicateurMesures));
        Assert.Equal(typeof(RedeploiementEcs), Assert.Single(services, s => s.ServiceType == typeof(IActionApresRotation)).ImplementationType);
    }

    private sealed class KmsEssai(ECDsa cle) : AmazonKeyManagementServiceClient(_anonyme, RegionEndpoint.CACentral1)
    {
        public SignRequest? Requete { get; private set; }

        public override Task<SignResponse> SignAsync(SignRequest request, CancellationToken cancellationToken = default)
        {
            Requete = request;
            var signature = cle.SignHash(request.Message.ToArray(), DSASignatureFormat.Rfc3279DerSequence);
            return Task.FromResult(new SignResponse { Signature = new MemoryStream(signature) });
        }

        public override Task<GetPublicKeyResponse> GetPublicKeyAsync(GetPublicKeyRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetPublicKeyResponse { PublicKey = new MemoryStream(cle.ExportSubjectPublicKeyInfo()) });
    }

    private sealed class CloudWatchEssai() : AmazonCloudWatchClient(_anonyme, RegionEndpoint.CACentral1)
    {
        public List<PutMetricDataRequest> Requetes { get; } = [];

        public override Task<PutMetricDataResponse> PutMetricDataAsync(PutMetricDataRequest request, CancellationToken cancellationToken = default)
        {
            Requetes.Add(request);
            return Task.FromResult(new PutMetricDataResponse());
        }
    }

    private sealed class EcsEssai() : AmazonECSClient(_anonyme, RegionEndpoint.CACentral1)
    {
        public List<UpdateServiceRequest> Requetes { get; } = [];

        public override Task<UpdateServiceResponse> UpdateServiceAsync(UpdateServiceRequest request, CancellationToken cancellationToken = default)
        {
            Requetes.Add(request);
            return Task.FromResult(new UpdateServiceResponse());
        }
    }
}
