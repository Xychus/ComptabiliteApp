using System.Security.Cryptography;
using System.Text;
using Ledger.Pki.Application.MotsDePasse;
using Ledger.Pki.Application.Rotation;
using Ledger.Pki.Infrastructure.Memoire;
using Ledger.SharedKernel;

namespace Ledger.Pki.Application.Tests.MotsDePasse;

/// <summary>Rotation du mot de passe d'un rôle PostgreSQL (Debezium), de bout en bout sur le magasin en mémoire.</summary>
public sealed class RotationMotDePasseTests
{
    private const string Secret = "ledger-dev/postgres/debezium";
    private const string Version1 = "v1";

    private static readonly Dictionary<string, string> _etiquettes = new(StringComparer.Ordinal)
    {
        [TypesSecret.Etiquette] = TypesSecret.MotDePassePostgres,
        [RolePostgres.Etiquette] = "debezium",
    };

    private readonly MagasinCertificatsMemoire _magasin = new();
    private readonly AdministrationEssai _administration = new();
    private readonly ActionEssai _redeploiement = new();

    public RotationMotDePasseTests() => _magasin.Creer(Secret, _etiquettes);

    private static CancellationToken Jeton => TestContext.Current.CancellationToken;

    private GestionnaireRotation Gestionnaire() => new(_magasin,
    [
        new CreationMotDePasse(_magasin),
        new DefinitionMotDePasse(_magasin, _administration),
        new TestMotDePasse(_magasin, _administration),
        new FinSecret(_magasin, [_redeploiement], TypesSecret.MotDePassePostgres),
    ]);

    private async Task<Result<string>[]> TournerAsync(string version)
    {
        _magasin.AmorcerRotation(Secret, version);
        var resultats = new List<Result<string>>();
        foreach (var etape in Enum.GetValues<EtapeRotation>())
        {
            resultats.Add(await Gestionnaire().TraiterAsync(new DemandeRotation(Secret, version, etape), Jeton));
        }

        return [.. resultats];
    }

    [Fact]
    public async Task Rotation_complete_applique_teste_promeut_et_redeploie()
    {
        var resultats = await TournerAsync(Version1);

        Assert.All(resultats, r => Assert.True(r.IsSuccess, r.ToString()));
        var courant = ContenuMotDePasse.DepuisJson((await _magasin.LireParEtiquetteAsync(Secret, EtiquettesVersion.Courante, Jeton))!)!;
        Assert.Equal("debezium", courant.Utilisateur);
        Assert.Equal(40, courant.MotDePasse.Length);
        Assert.Equal(courant.MotDePasse, _administration.MotsDePasse["debezium"]);
        Assert.Equal(1, _redeploiement.Executions);
    }

    [Fact]
    public async Task Deux_rotations_produisent_deux_mots_de_passe_differents()
    {
        await TournerAsync("v1");
        var premier = _administration.MotsDePasse["debezium"];

        await TournerAsync("v2");

        Assert.NotEqual(premier, _administration.MotsDePasse["debezium"]);
    }

    [Fact]
    public async Task Creation_rejouee_ne_change_pas_le_mot_de_passe()
    {
        _magasin.AmorcerRotation(Secret, Version1);
        var demande = new DemandeRotation(Secret, Version1, EtapeRotation.CreerSecret);
        await Gestionnaire().TraiterAsync(demande, Jeton);
        var cree = await _magasin.LireVersionAsync(Secret, Version1, Jeton);

        await Gestionnaire().TraiterAsync(demande, Jeton);

        Assert.Equal(cree, await _magasin.LireVersionAsync(Secret, Version1, Jeton));
    }

    [Fact]
    public async Task Test_echoue_si_la_connexion_est_refusee()
    {
        _administration.ConnexionAcceptee = false;

        var resultats = await TournerAsync(Version1);

        Assert.Equal("pki.mot-de-passe.connexion", resultats[2].Error.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Debezium; DROP ROLE x")]
    public async Task Role_absent_ou_invalide_est_refuse(string? role)
    {
        var etiquettes = new Dictionary<string, string>(StringComparer.Ordinal) { [TypesSecret.Etiquette] = TypesSecret.MotDePassePostgres };
        if (role is not null)
        {
            etiquettes[RolePostgres.Etiquette] = role;
        }

        _magasin.Creer("autre", etiquettes);
        _magasin.AmorcerRotation("autre", Version1);

        var resultat = await Gestionnaire().TraiterAsync(new DemandeRotation("autre", Version1, EtapeRotation.CreerSecret), Jeton);

        Assert.Equal("pki.mot-de-passe.role", resultat.Error.Code);
    }

    [Fact]
    public async Task Contenu_visant_un_autre_role_est_refuse()
    {
        _magasin.AmorcerRotation(Secret, Version1);
        await _magasin.EcrireEnAttenteAsync(Secret, Version1, new ContenuMotDePasse("ledger_app", "x").VersJson(), Jeton);

        var resultat = await Gestionnaire().TraiterAsync(new DemandeRotation(Secret, Version1, EtapeRotation.DefinirSecret), Jeton);

        Assert.Equal("pki.mot-de-passe.role", resultat.Error.Code);
        Assert.Empty(_administration.MotsDePasse);
    }

    [Fact]
    public async Task Contenu_illisible_est_refuse()
    {
        _magasin.AmorcerRotation(Secret, Version1);
        await _magasin.EcrireEnAttenteAsync(Secret, Version1, "pas du json", Jeton);

        var resultat = await Gestionnaire().TraiterAsync(new DemandeRotation(Secret, Version1, EtapeRotation.TesterSecret), Jeton);

        Assert.Equal("pki.rotation.contenu", resultat.Error.Code);
    }

    [Fact]
    public void ContenuMotDePasse_ne_divulgue_pas_le_mot_de_passe()
    {
        var contenu = new ContenuMotDePasse("debezium", "SECRET-123");

        Assert.DoesNotContain("SECRET-123", contenu.ToString(), StringComparison.Ordinal);
        Assert.Equal(contenu, ContenuMotDePasse.DepuisJson(contenu.VersJson()));
        Assert.Null(ContenuMotDePasse.DepuisJson("""{"utilisateur":"debezium"}"""));
    }

    [Fact]
    public void VerificateurScram_au_format_postgresql_et_conforme_a_la_rfc_5802()
    {
        var sel = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();

        var verificateur = VerificateurScram.Calculer("mot-de-passe", sel);

        var parties = verificateur.Split('$');
        Assert.Equal("SCRAM-SHA-256", parties[0]);
        Assert.Equal($"4096:{Convert.ToBase64String(sel)}", parties[1]);
        var sale = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes("mot-de-passe"), sel, 4096, HashAlgorithmName.SHA256, 32);
        var attendu = $"{Convert.ToBase64String(SHA256.HashData(HMACSHA256.HashData(sale, "Client Key"u8)))}:{Convert.ToBase64String(HMACSHA256.HashData(sale, "Server Key"u8))}";
        Assert.Equal(attendu, parties[2]);
        Assert.NotEqual(VerificateurScram.Calculer("mot-de-passe"), VerificateurScram.Calculer("mot-de-passe"));
    }

    private sealed class AdministrationEssai : IAdministrationRolesPostgres
    {
        public Dictionary<string, string> MotsDePasse { get; } = new(StringComparer.Ordinal);

        public bool ConnexionAcceptee { get; set; } = true;

        public Task DefinirMotDePasseAsync(string role, string motDePasse, CancellationToken cancellationToken)
        {
            MotsDePasse[role] = motDePasse;
            return Task.CompletedTask;
        }

        public Task<bool> TesterConnexionAsync(string role, string motDePasse, CancellationToken cancellationToken) =>
            Task.FromResult(ConnexionAcceptee && MotsDePasse.GetValueOrDefault(role) == motDePasse);
    }

    private sealed class ActionEssai : IActionApresRotation
    {
        public int Executions { get; private set; }

        public string Nom => "redéploiement";

        public bool SApplique(DescriptionSecret description) => true;

        public Task ExecuterAsync(DescriptionSecret description, string valeur, CancellationToken cancellationToken)
        {
            Executions++;
            return Task.CompletedTask;
        }
    }
}
