using System.Net;
using Ledger.Query.Api.Http;
using Ledger.Query.Api.Tests.Support;

namespace Ledger.Query.Api.Tests;

/// <summary>Jeton Keycloak exigé, scope <c>ledger:read</c> ; les sondes restent anonymes.</summary>
[Collection(GroupeApiEnMemoire.Nom)]
public sealed class AuthentificationTests(ApiEnMemoire api)
{
    private const string Balance = "/v1/trial-balance?entityId=ACME";

    [Fact]
    public async Task Sans_jeton_la_requete_est_un_401_en_problem_details() =>
        await api.Fabrique.CreateClient().LireAsync(Balance).ProblemeAsync(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task Un_jeton_signe_par_une_cle_inconnue_est_un_401() =>
        await api.Fabrique.CreerClientAuthentifie(JetonsEssai.Creer(cle: JetonsEssai.CleEtrangere)).LireAsync(Balance).ProblemeAsync(HttpStatusCode.Unauthorized);

    [Fact]
    public async Task Un_jeton_d_une_autre_audience_est_un_401() =>
        await api.Fabrique.CreerClientAuthentifie(JetonsEssai.Creer(audience: "autre-api")).LireAsync(Balance).ProblemeAsync(HttpStatusCode.Unauthorized);

    [Theory]
    [InlineData("openid ledger:write")]
    [InlineData(null)]
    public async Task Un_jeton_sans_le_scope_de_lecture_est_un_403(string? portee) =>
        await api.Fabrique.CreerClientAuthentifie(JetonsEssai.Creer(portee)).LireAsync(Balance).ProblemeAsync(HttpStatusCode.Forbidden);

    [Fact]
    public async Task Le_scope_de_lecture_donne_acces_aux_consultations()
    {
        var reponse = await api.Fabrique.CreerClientAuthentifie(JetonsEssai.Creer("ledger:write ledger:read")).LireAsync(Balance);

        Assert.Equal(HttpStatusCode.OK, reponse.StatusCode);
    }

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task Les_sondes_sont_anonymes(string sonde)
    {
        var reponse = await api.Fabrique.CreateClient().LireAsync(sonde);

        Assert.Equal(HttpStatusCode.OK, reponse.StatusCode);
    }

    [Fact]
    public async Task L_identifiant_de_correlation_fourni_est_renvoye_sinon_un_UUIDv7_est_genere()
    {
        var client = api.Client;
        var fourni = Guid.CreateVersion7();
        using var requete = new HttpRequestMessage(HttpMethod.Get, new Uri(Balance, UriKind.Relative));
        requete.Headers.Add(IntergicielCorrelation.NomEntete, fourni.ToString());

        var avecEntete = await client.SendAsync(requete, AssertionsHttp.Ct);
        var sansEntete = await client.LireAsync(Balance);

        Assert.Equal(fourni.ToString(), Assert.Single(avecEntete.Headers.GetValues(IntergicielCorrelation.NomEntete)));
        Assert.Equal(7, Guid.Parse(Assert.Single(sansEntete.Headers.GetValues(IntergicielCorrelation.NomEntete))).Version);
    }
}
