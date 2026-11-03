using System.Net;

namespace Ledger.Clients.Tests;

public sealed class FournisseurJetonsTests
{
    private readonly HorlogeFixe _horloge = new(Exemples.Maintenant);

    private FournisseurJetons Fournisseur(ServeurSimule serveur, OptionsClientGrandLivre? options = null) =>
        new(new HttpClient(serveur), options ?? Exemples.Options(), _horloge);

    [Fact]
    public async Task La_demande_est_un_client_credentials_sans_secret()
    {
        var serveur = new ServeurSimule((_, _) => ServeurSimule.Json(Exemples.ReponseJeton("j1")));
        using var jetons = Fournisseur(serveur);

        Assert.Equal("j1", await jetons.ObtenirAsync(TestContext.Current.CancellationToken));

        var (methode, autorisation, corps) = Assert.Single(serveur.Recues);
        Assert.Equal(HttpMethod.Post, methode);
        Assert.Null(autorisation);
        Assert.Equal("grant_type=client_credentials&client_id=ledger-exemple&scope=ledger%3Aread+ledger%3Awrite", corps);
        Assert.DoesNotContain("client_secret", corps, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sans_scope_demande_le_champ_est_omis()
    {
        var serveur = new ServeurSimule((_, _) => ServeurSimule.Json(Exemples.ReponseJeton("j1")));
        using var jetons = Fournisseur(serveur, Exemples.Options(portees: null));

        await jetons.ObtenirAsync(TestContext.Current.CancellationToken);

        Assert.Equal("grant_type=client_credentials&client_id=ledger-exemple", Assert.Single(serveur.Recues).Corps);
    }

    [Fact]
    public async Task Le_jeton_est_garde_jusqu_a_la_marge_puis_renouvele()
    {
        var serveur = new ServeurSimule((n, _) => ServeurSimule.Json(Exemples.ReponseJeton($"j{n}")));
        using var jetons = Fournisseur(serveur);
        var annulation = TestContext.Current.CancellationToken;

        Assert.Equal("j1", await jetons.ObtenirAsync(annulation));
        _horloge.Maintenant += TimeSpan.FromSeconds(269);
        Assert.Equal("j1", await jetons.ObtenirAsync(annulation));
        _horloge.Maintenant += TimeSpan.FromSeconds(1);
        Assert.Equal("j2", await jetons.ObtenirAsync(annulation));
    }

    [Fact]
    public async Task Les_appels_concurrents_partagent_une_seule_demande()
    {
        var serveur = new ServeurSimule((n, _) => ServeurSimule.Json(Exemples.ReponseJeton($"j{n}")));
        using var jetons = Fournisseur(serveur);

        var resultats = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => jetons.ObtenirAsync(TestContext.Current.CancellationToken)));

        Assert.All(resultats, j => Assert.Equal("j1", j));
        Assert.Single(serveur.Recues);
    }

    [Fact]
    public async Task Invalider_n_oublie_que_le_jeton_refuse()
    {
        var serveur = new ServeurSimule((n, _) => ServeurSimule.Json(Exemples.ReponseJeton($"j{n}")));
        using var jetons = Fournisseur(serveur);
        var annulation = TestContext.Current.CancellationToken;

        await jetons.ObtenirAsync(annulation);
        jetons.Invalider("autre");
        Assert.Equal("j1", await jetons.ObtenirAsync(annulation));
        jetons.Invalider("j1");
        Assert.Equal("j2", await jetons.ObtenirAsync(annulation));
    }

    [Fact]
    public async Task Un_refus_donne_le_statut_et_le_code_oauth()
    {
        var serveur = new ServeurSimule((_, _) => ServeurSimule.Json(
            """{"error":"unauthorized_client","error_description":"Client ledger-exemple : certificat absent"}""", HttpStatusCode.Unauthorized));
        using var jetons = Fournisseur(serveur);

        var echec = await Assert.ThrowsAsync<EchecJetonException>(() => jetons.ObtenirAsync(TestContext.Current.CancellationToken));

        Assert.Contains("401 unauthorized_client", echec.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("certificat absent", echec.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Un_refus_sans_corps_json_donne_le_statut_seul()
    {
        var serveur = new ServeurSimule((_, _) => new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("<html/>") });
        using var jetons = Fournisseur(serveur);

        var echec = await Assert.ThrowsAsync<EchecJetonException>(() => jetons.ObtenirAsync(TestContext.Current.CancellationToken));

        Assert.EndsWith(": 502", echec.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"expires_in":300}""")]
    [InlineData("""{"access_token":"j","expires_in":0}""")]
    public async Task Une_reponse_incomplete_est_un_echec(string json)
    {
        var serveur = new ServeurSimule((_, _) => ServeurSimule.Json(json));
        using var jetons = Fournisseur(serveur);

        await Assert.ThrowsAsync<EchecJetonException>(() => jetons.ObtenirAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Les_constructeurs_de_l_exception_gardent_le_message()
    {
        Assert.NotNull(new EchecJetonException().Message);
        Assert.Equal("m", new EchecJetonException("m", new InvalidOperationException()).Message);
    }
}
