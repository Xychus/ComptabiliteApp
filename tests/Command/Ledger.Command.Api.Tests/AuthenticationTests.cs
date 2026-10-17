using System.Net;
using System.Net.Http.Json;
using Ledger.Command.Api.Security;
using Ledger.Command.Api.Tests.Support;
using Ledger.Contracts.Http.V1;

namespace Ledger.Command.Api.Tests;

/// <summary>Défense en profondeur (ADR 0014) : jeton Keycloak valide, audience, scope et client exigés.</summary>
[Collection(InMemoryApiGroup.Name)]
public sealed class AuthenticationTests(InMemoryApi api)
{
    public static TheoryData<string> JetonsInvalides => new()
    {
        TestTokens.Create(key: TestTokens.ForeignKey()),
        TestTokens.Create(expires: DateTime.UtcNow.AddMinutes(-5)),
        TestTokens.Create(audience: "autre-api"),
        TestTokens.Create(issuer: "https://pirate.test/realms/ledger"),
        "pas-un-jeton",
    };

    [Fact]
    public async Task Un_appel_sans_jeton_renvoie_401_en_Problem_Details()
    {
        var client = new LedgerClient(api.Factory.CreateClient());

        var response = await client.PostAsync(Guid.CreateVersion7(), client.CashSale(10m));

        await response.ProblemAsync(HttpStatusCode.Unauthorized);
        Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
    }

    [Theory]
    [MemberData(nameof(JetonsInvalides))]
    public async Task Un_jeton_invalide_renvoie_401(string token)
    {
        var client = api.NewClient(token);

        var response = await client.PostAsync(Guid.CreateVersion7(), client.CashSale(10m));

        await response.ProblemAsync(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("openid profile")]
    [InlineData(null)]
    [InlineData("ledger:read")]
    public async Task Un_jeton_sans_le_scope_ledger_write_renvoie_403(string? scope)
    {
        var client = api.NewClient(TestTokens.Create(scope: scope));

        var response = await client.DefineAccountAsync("1000", "Encaisse", "Debit");

        await response.ProblemAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Un_jeton_sans_client_appelant_renvoie_403()
    {
        var client = api.NewClient(TestTokens.Create(client: null));

        var response = await client.DefineAccountAsync("1000", "Encaisse", "Debit");

        await response.ProblemAsync(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task L_acteur_des_evenements_est_le_client_azp_du_jeton()
    {
        var client = api.NewClient(TestTokens.Create(client: "comptabilite-batch"));

        var receipt = await (await client.DefineAccountAsync("1000", "Encaisse", "Debit").ExpectAsync(HttpStatusCode.Created)).BodyAsync<AccountResponse>();

        var stored = await api.Store.ReadStreamAsync(client.ChartStream, TestContext.Current.CancellationToken);
        Assert.Equal("comptabilite-batch", Assert.Single(stored).Metadata.Actor);
        Assert.Equal(receipt.SequenceNumber, stored[0].GlobalSequence);
    }

    [Theory]
    [InlineData("ledger:write", true)]
    [InlineData("openid ledger:write profile", true)]
    [InlineData("ledger:writer", false)]
    [InlineData("", false)]
    public void Le_scope_est_lu_dans_une_liste_separee_par_des_espaces(string scope, bool expected)
    {
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity([new System.Security.Claims.Claim("scope", scope)]));

        Assert.Equal(expected, LedgerAuthentication.HasScope(user, LedgerAuthentication.WriteScope));
    }

    [Fact]
    public void Le_scope_peut_venir_de_plusieurs_claims_scp()
    {
        var user = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim("scp", "openid"), new System.Security.Claims.Claim("scp", "ledger:write")]));

        Assert.True(LedgerAuthentication.HasScope(user, LedgerAuthentication.WriteScope));
    }

    [Fact]
    public async Task Les_sondes_de_sante_sont_anonymes()
    {
        var client = api.Factory.CreateClient();

        var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task Une_route_inconnue_renvoie_404_en_Problem_Details()
    {
        var client = api.NewClient();

        var response = await client.Http.PostAsJsonAsync("/v1/inconnu", new { }, TestContext.Current.CancellationToken);

        await response.ProblemAsync(HttpStatusCode.NotFound);
    }
}
