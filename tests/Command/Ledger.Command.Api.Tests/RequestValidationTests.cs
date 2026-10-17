using System.Net;
using System.Text;
using System.Text.Json;
using Ledger.Command.Api.Tests.Support;
using Ledger.Contracts.Http.V1;
using static Ledger.Command.Api.Tests.Support.LedgerClient;

namespace Ledger.Command.Api.Tests;

/// <summary>Requêtes mal formées : <c>400</c> en Problem Details, avant toute commande.</summary>
[Collection(InMemoryApiGroup.Name)]
public sealed class RequestValidationTests(InMemoryApi api)
{
    [Fact]
    public async Task Un_identifiant_d_ecriture_qui_n_est_pas_un_UUIDv7_est_refuse()
    {
        var client = api.NewClient();

        var problem = await (await client.PostAsync(Guid.NewGuid(), client.CashSale(10m))).ProblemAsync(HttpStatusCode.BadRequest);

        Assert.True(problem.GetProperty("errors").TryGetProperty("entryId", out _));
    }

    [Fact]
    public async Task Les_erreurs_de_forme_sont_toutes_renvoyees_par_champ()
    {
        var client = api.NewClient();
        var request = new PostJournalEntryRequest(
            "entité invalide",
            March15,
            [
                new JournalLineDto("1000", "Débit", new MoneyDto("12,50", "CAD")),
                new JournalLineDto("", "Credit", new MoneyDto("12.50", "XYZ")),
            ],
            new string('x', 501));

        var problem = await (await client.PostAsync(Guid.CreateVersion7(), request)).ProblemAsync(HttpStatusCode.BadRequest);

        var errors = problem.GetProperty("errors");
        Assert.Equal(
            ["description", "entityId", "lines[0].amount.amount", "lines[0].side", "lines[1].accountId", "lines[1].amount.currency"],
            errors.EnumerateObject().Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.Equal("urn:ledger:problem:InvalidRequest", problem.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"entityId":"ACME","businessDate":"2026-03-15"}""")]
    [InlineData("""{"entityId":null,"businessDate":"2026-03-15","lines":[]}""")]
    [InlineData("""{"entityId":"ACME","businessDate":"15/03/2026","lines":[]}""")]
    [InlineData("""{"entityId":"ACME","businessDate":"2026-03-15","lines":[{"accountId":"1000","side":"Debit","amount":12.5}]}""")]
    public async Task Un_corps_illisible_ou_incomplet_est_refuse(string body)
    {
        var client = api.NewClient();

        var response = await client.Http.PutAsync(new Uri($"/v1/journal-entries/{Guid.CreateVersion7()}", UriKind.Relative), new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        var problem = await response.ProblemAsync(HttpStatusCode.BadRequest);
        Assert.Equal("Requête invalide", problem.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task Un_identifiant_de_chemin_mal_forme_est_refuse()
    {
        var client = api.NewClient();

        var response = await client.Http.PutAsync(new Uri("/v1/journal-entries/pas-un-uuid", UriKind.Relative), new StringContent(JsonSerializer.Serialize(client.CashSale(1m), JsonSerializerOptions.Web), Encoding.UTF8, "application/json"), Ct);

        await response.ProblemAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Un_nombre_en_chaine_n_est_pas_accepte_pour_un_entier()
    {
        var client = api.NewClient();
        var body = $$"""{"entityId":"{{client.Entity}}","expectedVersion":"0"}""";

        var response = await client.Http.PostAsync(new Uri("/v1/periods/2026-03/open", UriKind.Relative), new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        await response.ProblemAsync(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Une_periode_et_une_version_invalides_sont_refusees()
    {
        var client = api.NewClient();

        var problem = await (await client.OpenAsync("2026-13", expectedVersion: -1)).ProblemAsync(HttpStatusCode.BadRequest);

        Assert.Equal(["expectedVersion", "period"], problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Un_compte_mal_defini_est_refuse()
    {
        var client = api.NewClient();

        var problem = await (await client.DefineAccountAsync("10 00", "Encaisse", "Actif", currency: "cad")).ProblemAsync(HttpStatusCode.BadRequest);

        Assert.Equal(["accountId", "currency", "normalSide"], problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Une_contre_passation_mal_formee_est_refusee()
    {
        var client = api.NewClient();

        var problem = await (await client.ReverseAsync(Guid.NewGuid(), Guid.NewGuid(), description: new string('x', 501))).ProblemAsync(HttpStatusCode.BadRequest);

        Assert.Equal(["description", "entryId", "reversalEntryId"], problem.GetProperty("errors").EnumerateObject().Select(e => e.Name).Order(StringComparer.Ordinal));
    }
}
