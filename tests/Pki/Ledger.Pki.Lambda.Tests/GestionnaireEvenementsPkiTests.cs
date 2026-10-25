using System.Text.Json;
using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Inventaire;
using Ledger.Pki.Application.Profils;
using Ledger.Pki.Application.Rotation;
using Ledger.Pki.Infrastructure.Memoire;
using Ledger.Pki.Tests.Support;
using Ledger.SharedKernel;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Pki.Lambda.Tests;

public sealed class GestionnaireEvenementsPkiTests : IDisposable
{
    private const string Secret = "ledger-dev/pki/projections-worker";

    private readonly AutoriteEssai _pki = new();
    private readonly MagasinCertificatsMemoire _magasin = new();
    private readonly PublicateurMesuresMemoire _publicateur = new();
    private readonly ServiceProvider _fournisseur;

    public GestionnaireEvenementsPkiTests()
    {
        _magasin.Creer(Secret, new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ProfilCertificat.EtiquetteNomCommun] = "projections-worker",
            [ProfilCertificat.EtiquetteUsage] = "client",
        });

        var options = new OptionsPki(_pki.Intermediaire.ExportCertificatePem(), _pki.Racine.ExportCertificatePem(), AutoriteEssai.Cle);
        var services = new ServiceCollection()
            .AddSingleton<ISignataireAutorite>(_pki.Signataire)
            .AddSingleton<IMagasinCertificats>(_magasin)
            .AddSingleton<ISourceInventaire>(_magasin)
            .AddSingleton<IPublicateurMesures>(_publicateur)
            .AjouterPki(options);
        services.AddSingleton<IClock>(_pki.Horloge);
        _fournisseur = services.BuildServiceProvider();
    }

    public void Dispose()
    {
        _fournisseur.Dispose();
        _pki.Dispose();
    }

    private GestionnaireEvenementsPki Gestionnaire => _fournisseur.GetRequiredService<GestionnaireEvenementsPki>();

    private static EvenementPki Lire(string json) => JsonSerializer.Deserialize(json, ContexteJsonPki.Default.EvenementPki)!;

    [Fact]
    public async Task Evenements_de_rotation_de_Secrets_Manager_emettent_le_certificat()
    {
        _magasin.AmorcerRotation(Secret, "jeton-1");

        foreach (var etape in new[] { "createSecret", "setSecret", "testSecret", "finishSecret" })
        {
            await Gestionnaire.TraiterAsync(
                Lire($$"""{"SecretId":"{{Secret}}","ClientRequestToken":"jeton-1","Step":"{{etape}}"}"""),
                TestContext.Current.CancellationToken);
        }

        var courant = await _magasin.LireParEtiquetteAsync(Secret, EtiquettesVersion.Courante, TestContext.Current.CancellationToken);
        Assert.Equal(AutoriteEssai.Maintenant.AddDays(7), ContenuSecretCertificat.DepuisJson(courant!)!.ExpireLe);
    }

    [Fact]
    public async Task Echec_de_rotation_leve_une_exception_pour_Secrets_Manager()
    {
        var erreur = await Assert.ThrowsAsync<EchecPkiException>(() => Gestionnaire.TraiterAsync(
            Lire($$"""{"SecretId":"{{Secret}}","ClientRequestToken":"inconnu","Step":"createSecret"}"""),
            TestContext.Current.CancellationToken));

        Assert.StartsWith("pki.rotation.version-inconnue", erreur.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Etape_inconnue_est_refusee() =>
        await Assert.ThrowsAsync<EchecPkiException>(() => Gestionnaire.TraiterAsync(
            Lire($$"""{"SecretId":"{{Secret}}","ClientRequestToken":"jeton-1","Step":"autre"}"""),
            TestContext.Current.CancellationToken));

    [Fact]
    public async Task Operation_inventaire_publie_les_echeances()
    {
        var reponse = await Gestionnaire.TraiterAsync(Lire("""{"operation":"inventaire"}"""), TestContext.Current.CancellationToken);

        Assert.Contains("projections-worker=0 j", reponse.Message, StringComparison.Ordinal);
        Assert.Contains(_publicateur.Publiees, m => m.Identite == InventaireEcheances.IdentiteIntermediaire);
    }

    [Fact]
    public async Task Operation_demande_signature_renvoie_une_csr()
    {
        const string NouvelleCle = "arn:aws:kms:ca-central-1:111111111111:key/2027";
        _pki.Signataire.CreerCle(NouvelleCle);

        var reponse = await Gestionnaire.TraiterAsync(
            Lire($$"""{"operation":"demande-signature-intermediaire","cle":"{{NouvelleCle}}","sujet":"CN=AC 2027"}"""),
            TestContext.Current.CancellationToken);

        Assert.StartsWith("-----BEGIN CERTIFICATE REQUEST-----", reponse.Csr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"operation":"demande-signature-intermediaire","sujet":"CN=AC"}""")]
    [InlineData("""{"operation":"demande-signature-intermediaire","cle":"arn:cle"}""")]
    [InlineData("""{"operation":"autre"}""")]
    [InlineData("""{}""")]
    public async Task Evenement_incomplet_ou_inconnu_est_refuse(string json) =>
        await Assert.ThrowsAsync<EchecPkiException>(() => Gestionnaire.TraiterAsync(Lire(json), TestContext.Current.CancellationToken));

    [Fact]
    public async Task Demande_signature_avec_sujet_invalide_est_refusee()
    {
        const string NouvelleCle = "arn:aws:kms:ca-central-1:111111111111:key/invalide";
        _pki.Signataire.CreerCle(NouvelleCle);

        await Assert.ThrowsAsync<EchecPkiException>(() => Gestionnaire.TraiterAsync(
            Lire($$"""{"operation":"demande-signature-intermediaire","cle":"{{NouvelleCle}}","sujet":"pas=un=nom,,"}"""),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Reponse_serialisee_en_json()
    {
        var json = JsonSerializer.Serialize(new ReponsePki { Message = "ok", Csr = "csr" }, ContexteJsonPki.Default.ReponsePki);

        Assert.Equal("""{"message":"ok","csr":"csr"}""", json);
    }
}

public sealed class CompositionPkiTests
{
    [Fact]
    public void OptionsPki_lues_dans_l_environnement()
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OptionsPki.VariableIntermediaire] = "i",
            [OptionsPki.VariableRacine] = "r",
            [OptionsPki.VariableCle] = "c",
        };

        Assert.Equal(new OptionsPki("i", "r", "c"), OptionsPki.DepuisEnvironnement(variables.GetValueOrDefault));
    }

    [Fact]
    public void OptionsPki_sans_aucune_variable_mode_ceremonie() =>
        Assert.False(OptionsPki.DepuisEnvironnement(_ => null).AutoriteInstallee);

    [Fact]
    public void OptionsPki_configuration_partielle_est_refusee()
    {
        var erreur = Assert.Throws<InvalidOperationException>(() =>
            OptionsPki.DepuisEnvironnement(v => v == OptionsPki.VariableCle ? "arn:cle" : null));

        Assert.Contains(OptionsPki.VariableIntermediaire, erreur.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mode_ceremonie_produit_une_csr_mais_refuse_rotation_et_inventaire()
    {
        using var signataire = new SignataireMemoire();
        signataire.CreerCle("arn:cle");
        using var fournisseur = new ServiceCollection()
            .AddSingleton<ISignataireAutorite>(signataire)
            .AjouterPki(new OptionsPki(null, null, null))
            .BuildServiceProvider();
        var gestionnaire = fournisseur.GetRequiredService<GestionnaireEvenementsPki>();

        var reponse = await gestionnaire.TraiterAsync(
            new EvenementPki { Operation = "demande-signature-intermediaire", Cle = "arn:cle", Sujet = "CN=AC" },
            TestContext.Current.CancellationToken);

        Assert.NotNull(reponse.Csr);
        await Assert.ThrowsAsync<EchecPkiException>(() => gestionnaire.TraiterAsync(
            new EvenementPki { Operation = "inventaire" }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<EchecPkiException>(() => gestionnaire.TraiterAsync(
            new EvenementPki { SecretId = "s", ClientRequestToken = "v", Step = "createSecret" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AjouterPki_autorite_invalide_empeche_le_demarrage() =>
        Assert.Throws<EchecPkiException>(() => new ServiceCollection().AjouterPki(new OptionsPki("x", "y", "z")));

    [Fact]
    public void EchecPkiException_constructeurs()
    {
        Assert.NotNull(new EchecPkiException().Message);
        Assert.IsType<InvalidOperationException>(new EchecPkiException("m", new InvalidOperationException()).InnerException);
    }
}
