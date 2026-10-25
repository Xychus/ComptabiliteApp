using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Profils;
using Ledger.Pki.Application.Rotation;
using Ledger.Pki.Infrastructure.Memoire;
using Ledger.Pki.Tests.Support;

namespace Ledger.Pki.Application.Tests.Rotation;

/// <summary>Protocole de rotation de Secrets Manager, de bout en bout sur le magasin en mémoire.</summary>
public sealed class GestionnaireRotationTests : IDisposable
{
    private const string Secret = "ledger-dev/pki/debezium";
    private const string Version1 = "00000000-0000-0000-0000-000000000001";
    private const string Version2 = "00000000-0000-0000-0000-000000000002";

    private static readonly Dictionary<string, string> _etiquettes = new(StringComparer.Ordinal)
    {
        [ProfilCertificat.EtiquetteNomCommun] = "debezium",
        [ProfilCertificat.EtiquetteUsage] = "client",
        ["pki:services"] = "debezium",
    };

    private readonly AutoriteEssai _pki = new();
    private readonly MagasinCertificatsMemoire _magasin = new();
    private readonly ActionEssai _action = new();

    public GestionnaireRotationTests() => _magasin.Creer(Secret, _etiquettes);

    public void Dispose() => _pki.Dispose();

    private GestionnaireRotation Gestionnaire() => new(_magasin,
    [
        new CreationSecret(_magasin, _pki.Emetteur()),
        new DefinitionSecret(),
        new TestSecret(_magasin, _pki.Verificateur()),
        new FinSecret(_magasin, [_action]),
    ]);

    private async Task TournerAsync(string version)
    {
        _magasin.AmorcerRotation(Secret, version);
        foreach (var etape in Enum.GetValues<EtapeRotation>())
        {
            var resultat = await Gestionnaire().TraiterAsync(new DemandeRotation(Secret, version, etape), TestContext.Current.CancellationToken);
            Assert.True(resultat.IsSuccess, resultat.ToString());
        }
    }

    [Fact]
    public async Task Rotation_complete_rend_courant_un_certificat_valide()
    {
        await TournerAsync(Version1);

        var courant = ContenuSecretCertificat.DepuisJson((await _magasin.LireParEtiquetteAsync(Secret, EtiquettesVersion.Courante, TestContext.Current.CancellationToken))!);
        Assert.NotNull(courant);
        Assert.True(_pki.Verificateur().Verifier(courant, ProfilCertificat.DepuisEtiquettes(_etiquettes).Value).IsSuccess);
        Assert.Equal(1, _action.Executions);
    }

    [Fact]
    public async Task Seconde_rotation_remplace_la_version_courante()
    {
        await TournerAsync(Version1);
        var premiere = await _magasin.LireVersionAsync(Secret, Version1, TestContext.Current.CancellationToken);

        await TournerAsync(Version2);

        var description = await _magasin.DecrireAsync(Secret, TestContext.Current.CancellationToken);
        Assert.Equal(Version2, description.VersionPortant(EtiquettesVersion.Courante));
        Assert.NotEqual(premiere, await _magasin.LireParEtiquetteAsync(Secret, EtiquettesVersion.Courante, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreerSecret_rejoue_ne_reemet_pas()
    {
        _magasin.AmorcerRotation(Secret, Version1);
        var demande = new DemandeRotation(Secret, Version1, EtapeRotation.CreerSecret);
        await Gestionnaire().TraiterAsync(demande, TestContext.Current.CancellationToken);
        var emise = await _magasin.LireVersionAsync(Secret, Version1, TestContext.Current.CancellationToken);

        var rejeu = await Gestionnaire().TraiterAsync(demande, TestContext.Current.CancellationToken);

        Assert.True(rejeu.IsSuccess);
        Assert.Equal(emise, await _magasin.LireVersionAsync(Secret, Version1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Rotation_inactive_est_refusee()
    {
        _magasin.Creer("inactif", _etiquettes, rotationActivee: false);
        _magasin.AmorcerRotation("inactif", Version1);

        var resultat = await Gestionnaire().TraiterAsync(new DemandeRotation("inactif", Version1, EtapeRotation.CreerSecret), TestContext.Current.CancellationToken);

        Assert.Equal("pki.rotation.inactive", resultat.Error.Code);
    }

    [Fact]
    public async Task Version_inconnue_est_refusee()
    {
        var resultat = await Gestionnaire().TraiterAsync(new DemandeRotation(Secret, Version1, EtapeRotation.CreerSecret), TestContext.Current.CancellationToken);

        Assert.Equal("pki.rotation.version-inconnue", resultat.Error.Code);
    }

    [Fact]
    public async Task Version_ni_courante_ni_en_attente_est_refusee()
    {
        await TournerAsync(Version1);
        await TournerAsync(Version2);

        var resultat = await Gestionnaire().TraiterAsync(new DemandeRotation(Secret, Version1, EtapeRotation.TesterSecret), TestContext.Current.CancellationToken);

        Assert.Equal("pki.rotation.etat", resultat.Error.Code);
    }

    [Fact]
    public async Task Etape_sur_version_deja_courante_ne_fait_rien()
    {
        await TournerAsync(Version1);

        var resultat = await Gestionnaire().TraiterAsync(new DemandeRotation(Secret, Version1, EtapeRotation.CreerSecret), TestContext.Current.CancellationToken);

        Assert.True(resultat.IsSuccess);
        Assert.Contains("déjà courante", resultat.Value, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TerminerSecret_rejoue_relance_les_actions_sans_repromouvoir()
    {
        await TournerAsync(Version1);

        var resultat = await Gestionnaire().TraiterAsync(new DemandeRotation(Secret, Version1, EtapeRotation.TerminerSecret), TestContext.Current.CancellationToken);

        Assert.True(resultat.IsSuccess);
        Assert.Equal(2, _action.Executions);
    }

    [Fact]
    public async Task TerminerSecret_sans_action_applicable_promeut_seulement()
    {
        _action.Applicable = false;

        await TournerAsync(Version1);

        Assert.Equal(0, _action.Executions);
    }

    [Fact]
    public async Task CreerSecret_profil_invalide_est_refuse()
    {
        _magasin.Creer("sans-profil", new Dictionary<string, string>(StringComparer.Ordinal));
        _magasin.AmorcerRotation("sans-profil", Version1);

        var resultat = await Gestionnaire().TraiterAsync(new DemandeRotation("sans-profil", Version1, EtapeRotation.CreerSecret), TestContext.Current.CancellationToken);

        Assert.Equal("pki.profil.nom-commun", resultat.Error.Code);
    }

    [Fact]
    public async Task CreerSecret_intermediaire_echue_est_refusee()
    {
        using var pki = new AutoriteEssai(validiteIntermediaire: TimeSpan.FromHours(12));
        _magasin.AmorcerRotation(Secret, Version1);
        var etape = new CreationSecret(_magasin, pki.Emetteur());

        var resultat = await etape.ExecuterAsync(
            new DemandeRotation(Secret, Version1, EtapeRotation.CreerSecret),
            await _magasin.DecrireAsync(Secret, TestContext.Current.CancellationToken),
            TestContext.Current.CancellationToken);

        Assert.Equal("pki.emission.intermediaire-echu", resultat.Error.Code);
    }

    [Fact]
    public async Task TesterSecret_contenu_illisible_est_refuse()
    {
        _magasin.AmorcerRotation(Secret, Version1);
        await _magasin.EcrireEnAttenteAsync(Secret, Version1, "pas du json", TestContext.Current.CancellationToken);

        var resultat = await Gestionnaire().TraiterAsync(new DemandeRotation(Secret, Version1, EtapeRotation.TesterSecret), TestContext.Current.CancellationToken);

        Assert.Equal("pki.rotation.contenu", resultat.Error.Code);
    }

    [Fact]
    public async Task TerminerSecret_contenu_illisible_est_refuse()
    {
        _magasin.AmorcerRotation(Secret, Version1);
        await _magasin.EcrireEnAttenteAsync(Secret, Version1, "pas du json", TestContext.Current.CancellationToken);

        var resultat = await Gestionnaire().TraiterAsync(new DemandeRotation(Secret, Version1, EtapeRotation.TerminerSecret), TestContext.Current.CancellationToken);

        Assert.Equal("pki.rotation.contenu", resultat.Error.Code);
    }

    [Fact]
    public async Task Etape_non_enregistree_est_refusee()
    {
        _magasin.AmorcerRotation(Secret, Version1);

        var resultat = await new GestionnaireRotation(_magasin, [])
            .TraiterAsync(new DemandeRotation(Secret, Version1, EtapeRotation.DefinirSecret), TestContext.Current.CancellationToken);

        Assert.Equal("pki.rotation.etape", resultat.Error.Code);
    }

    private sealed class ActionEssai : IActionApresRotation
    {
        public bool Applicable { get; set; } = true;

        public int Executions { get; private set; }

        public string Nom => "essai";

        public bool SApplique(DescriptionSecret description) => Applicable;

        public Task ExecuterAsync(DescriptionSecret description, ContenuSecretCertificat contenu, CancellationToken cancellationToken)
        {
            Executions++;
            return Task.CompletedTask;
        }
    }
}
