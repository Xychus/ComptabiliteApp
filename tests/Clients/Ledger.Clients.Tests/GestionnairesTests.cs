using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Ledger.Clients.Tests;

public sealed class GestionnaireJetonTests
{
    [Fact]
    public async Task Chaque_requete_porte_le_jeton()
    {
        var api = new ServeurSimule((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = Client(api, new ServeurSimule((n, _) => ServeurSimule.Json(Exemples.ReponseJeton($"j{n}"))));

        using var reponse = await client.GetAsync(new Uri("https://api/v1/trial-balance"), TestContext.Current.CancellationToken);

        Assert.Equal("Bearer j1", Assert.Single(api.Recues).Autorisation);
    }

    [Fact]
    public async Task Un_401_renouvelle_le_jeton_et_renvoie_le_meme_corps_une_seule_fois()
    {
        var api = new ServeurSimule((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client = Client(api, new ServeurSimule((n, _) => ServeurSimule.Json(Exemples.ReponseJeton($"j{n}"))));

        using var reponse = await client.PutAsync(new Uri("https://api/v1/journal-entries/1"), new StringContent("{\"a\":1}", Encoding.UTF8), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, reponse.StatusCode);
        Assert.Equal(["Bearer j1", "Bearer j2"], api.Recues.Select(r => r.Autorisation));
        Assert.All(api.Recues, r => Assert.Equal("{\"a\":1}", r.Corps));
    }

    private static HttpClient Client(ServeurSimule api, ServeurSimule keycloak) =>
        new(new GestionnaireJeton(new FournisseurJetons(new HttpClient(keycloak), Exemples.Options(), new HorlogeFixe(Exemples.Maintenant))) { InnerHandler = api });
}

public sealed class GestionnaireReessai429Tests
{
    private readonly List<TimeSpan> _attentes = [];

    private HttpClient Client(ServeurSimule api, int tentatives = 5, double tirage = 1.0) =>
        new(new GestionnaireReessai429(Exemples.Options(tentatives: tentatives), (delai, _) => { _attentes.Add(delai); return Task.CompletedTask; }, () => tirage) { InnerHandler = api });

    private static HttpResponseMessage TropDeRequetes(RetryConditionHeaderValue? retryAfter = null)
    {
        var reponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        reponse.Headers.RetryAfter = retryAfter;
        return reponse;
    }

    [Fact]
    public async Task Le_recul_est_exponentiel_borne_et_s_arrete_au_maximum_de_tentatives()
    {
        var api = new ServeurSimule((_, _) => TropDeRequetes());
        using var client = Client(api, tentatives: 9);

        using var reponse = await client.GetAsync(new Uri("https://api/v1/x"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, reponse.StatusCode);
        Assert.Equal(9, api.Recues.Count);
        int[] attendusMs = [200, 400, 800, 1600, 3200, 6400, 10000, 10000];
        Assert.Equal(attendusMs.Select(ms => TimeSpan.FromMilliseconds(ms)), _attentes);
    }

    [Fact]
    public async Task La_gigue_tire_le_delai_entre_zero_et_le_plafond()
    {
        var api = new ServeurSimule((n, _) => n == 1 ? TropDeRequetes() : new HttpResponseMessage(HttpStatusCode.OK));
        using var client = Client(api, tirage: 0.25);

        using var reponse = await client.GetAsync(new Uri("https://api/v1/x"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, reponse.StatusCode);
        Assert.Equal([TimeSpan.FromMilliseconds(50)], _attentes);
    }

    [Fact]
    public async Task Retry_after_en_secondes_est_respecte_et_borne()
    {
        var api = new ServeurSimule((n, _) => n switch
        {
            1 => TropDeRequetes(new RetryConditionHeaderValue(TimeSpan.FromSeconds(3))),
            2 => TropDeRequetes(new RetryConditionHeaderValue(TimeSpan.FromMinutes(5))),
            _ => new HttpResponseMessage(HttpStatusCode.OK),
        });
        using var client = Client(api);

        using var reponse = await client.GetAsync(new Uri("https://api/v1/x"), TestContext.Current.CancellationToken);

        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10)], _attentes);
    }

    [Fact]
    public async Task Retry_after_en_date_est_relatif_a_la_date_de_la_reponse()
    {
        var date = new DateTimeOffset(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var api = new ServeurSimule((n, _) =>
        {
            if (n > 2)
            {
                return new HttpResponseMessage(HttpStatusCode.OK);
            }

            var reponse = TropDeRequetes(new RetryConditionHeaderValue(n == 1 ? date.AddSeconds(2) : date.AddSeconds(-2)));
            reponse.Headers.Date = date;
            return reponse;
        });
        using var client = Client(api);

        using var reponse = await client.GetAsync(new Uri("https://api/v1/x"), TestContext.Current.CancellationToken);

        Assert.Equal([TimeSpan.FromSeconds(2), TimeSpan.Zero], _attentes);
    }

    [Fact]
    public async Task Une_ecriture_est_renvoyee_avec_le_meme_corps()
    {
        var api = new ServeurSimule((n, _) => n == 1 ? TropDeRequetes() : new HttpResponseMessage(HttpStatusCode.Created));
        using var client = Client(api);

        using var reponse = await client.PostAsync(new Uri("https://api/v1/periods/2030-01/seal"), new StringContent("{\"expectedVersion\":3}"), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Created, reponse.StatusCode);
        Assert.Equal(2, api.Recues.Count);
        Assert.All(api.Recues, r => Assert.Equal("{\"expectedVersion\":3}", r.Corps));
    }

    [Theory]
    [InlineData(HttpStatusCode.OK)]
    [InlineData(HttpStatusCode.Conflict)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Un_autre_statut_n_est_jamais_retente(HttpStatusCode statut)
    {
        var api = new ServeurSimule((_, _) => new HttpResponseMessage(statut));
        using var client = Client(api);

        using var reponse = await client.GetAsync(new Uri("https://api/v1/x"), TestContext.Current.CancellationToken);

        Assert.Equal(statut, reponse.StatusCode);
        Assert.Single(api.Recues);
        Assert.Empty(_attentes);
    }

    [Fact]
    public void Au_moins_une_tentative_est_exigee()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GestionnaireReessai429(Exemples.Options(tentatives: 0)));
    }
}

public sealed class ClientGrandLivreTests
{
    [Fact]
    public void Le_client_vise_l_adresse_de_l_api()
    {
        using var grandLivre = ClientGrandLivre.Creer(Exemples.Options());

        Assert.Equal(new Uri("https://api.ledger-test.internal/v1/"), grandLivre.Http.BaseAddress);
    }

    [Fact]
    public void La_racine_de_la_pki_est_acceptee()
    {
        var (racine, _, _) = ConfianceRacinePkiTests.Pki("keycloak");
        var options = Exemples.Options();

        using var grandLivre = ClientGrandLivre.Creer(new OptionsClientGrandLivre
        {
            PointTerminaisonJetons = options.PointTerminaisonJetons,
            ClientId = options.ClientId,
            CertificatClient = options.CertificatClient,
            RacinePki = racine,
            AdresseApi = options.AdresseApi,
        });

        Assert.NotNull(grandLivre.Http);
    }
}
