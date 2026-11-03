using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Ledger.Authorizer.Tests;

public sealed class ValidateurJetonsTests : IDisposable
{
    private readonly RealmDeTest _realm = new();

    public void Dispose() => _realm.Dispose();

    [Fact]
    public async Task Un_jeton_valide_donne_le_client_et_ses_scopes()
    {
        var valide = await _realm.Validateur().ValiderAsync(_realm.Jeton(scope: "ledger:read profile"), TestContext.Current.CancellationToken);

        Assert.NotNull(valide);
        Assert.Equal("ledger-exemple", valide.ClientId);
        Assert.Equal(["ledger:read", "profile"], valide.Portees.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Un_jeton_sans_scope_est_valide_sans_portee()
    {
        var valide = await _realm.Validateur().ValiderAsync(_realm.Jeton(scope: null), TestContext.Current.CancellationToken);

        Assert.NotNull(valide);
        Assert.Empty(valide.Portees);
    }

    [Fact]
    public async Task Un_jeton_expire_au_dela_de_la_tolerance_est_refuse()
    {
        var jeton = _realm.Jeton(emisLe: RealmDeTest.Maintenant.AddMinutes(-6), duree: TimeSpan.FromMinutes(5));

        Assert.Null(await _realm.Validateur().ValiderAsync(jeton, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Un_jeton_expire_depuis_moins_de_30_secondes_est_tolere()
    {
        var jeton = _realm.Jeton(emisLe: RealmDeTest.Maintenant.AddSeconds(-320), duree: TimeSpan.FromMinutes(5));

        Assert.NotNull(await _realm.Validateur().ValiderAsync(jeton, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Un_jeton_emis_dans_le_futur_est_refuse()
    {
        var jeton = _realm.Jeton(emisLe: RealmDeTest.Maintenant.AddMinutes(2));

        Assert.Null(await _realm.Validateur().ValiderAsync(jeton, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Une_mauvaise_audience_est_refusee()
    {
        Assert.Null(await _realm.Validateur().ValiderAsync(_realm.Jeton(audience: "account"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Un_mauvais_emetteur_est_refuse()
    {
        var jeton = _realm.Jeton(emetteur: "https://keycloak.ledger-test.internal/realms/autre");

        Assert.Null(await _realm.Validateur().ValiderAsync(jeton, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Une_signature_par_une_autre_cle_portant_le_meme_kid_est_refusee()
    {
        using var cleImposteur = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var imposteur = new ECDsaSecurityKey(cleImposteur) { KeyId = _realm.Cle.KeyId };
        var jeton = _realm.Jeton(signature: new SigningCredentials(imposteur, SecurityAlgorithms.EcdsaSha256));

        Assert.Null(await _realm.Validateur().ValiderAsync(jeton, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Une_signature_symetrique_est_refusee()
    {
        var secret = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string('s', 64))) { KeyId = _realm.Cle.KeyId };
        var jeton = _realm.Jeton(signature: new SigningCredentials(secret, SecurityAlgorithms.HmacSha256));

        Assert.Null(await _realm.Validateur().ValiderAsync(jeton, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Un_jeton_non_signe_est_refuse()
    {
        var jeton = _realm.Jeton();
        var nonSigne = string.Join('.', Base64UrlEncoder.Encode("""{"alg":"none","typ":"JWT"}"""), jeton.Split('.')[1], string.Empty);

        Assert.Null(await _realm.Validateur().ValiderAsync(nonSigne, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Un_jeton_mal_forme_est_refuse()
    {
        Assert.Null(await _realm.Validateur().ValiderAsync("pas-un-jwt", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Un_jeton_sans_azp_est_refuse()
    {
        Assert.Null(await _realm.Validateur().ValiderAsync(_realm.Jeton(azp: null), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Une_cle_inconnue_force_une_seule_relecture_du_jwks()
    {
        using var nouveauRealm = new RealmDeTest("cle-2");
        var configuration = new ConfigurationAvecRotation(_realm.Configuration(), nouveauRealm.Configuration());

        var valide = await _realm.Validateur(configuration).ValiderAsync(nouveauRealm.Jeton(), TestContext.Current.CancellationToken);

        Assert.NotNull(valide);
        Assert.Equal(1, configuration.Relectures);
    }

    [Fact]
    public async Task Un_jeton_valide_ne_provoque_aucune_relecture()
    {
        using var autreRealm = new RealmDeTest("cle-2");
        var configuration = new ConfigurationAvecRotation(_realm.Configuration(), autreRealm.Configuration());

        Assert.NotNull(await _realm.Validateur(configuration).ValiderAsync(_realm.Jeton(), TestContext.Current.CancellationToken));
        Assert.Equal(0, configuration.Relectures);
    }
}
