using System.Net;
using Ledger.Command.Api.Tests.Support;
using Ledger.Contracts.Http.V1;
using static Ledger.Command.Api.Tests.Support.LedgerClient;

namespace Ledger.Command.Api.Tests;

/// <summary><c>PUT /v1/accounts/{accountId}</c> : plan comptable tenu dans le journal (ADR 0012).</summary>
[Collection(InMemoryApiGroup.Name)]
public sealed class AccountEndpointTests(InMemoryApi api)
{
    [Fact]
    public async Task Definir_un_compte_le_cree_puis_le_rejeu_renvoie_le_meme_evenement()
    {
        var client = api.NewClient();

        var created = await client.DefineAccountAsync(Cash, "Encaisse", "Debit").ExpectAsync(HttpStatusCode.Created);
        var replayed = await client.DefineAccountAsync(Cash, "Encaisse", "Debit").ExpectAsync(HttpStatusCode.OK);

        var first = await created.BodyAsync<AccountResponse>();
        Assert.Equal((client.Entity, Cash, 1L), (first.EntityId, first.AccountId, first.ChartVersion));
        Assert.Equal($"/v1/accounts/{Cash}", created.Headers.Location?.OriginalString);
        Assert.Equal(first, await replayed.BodyAsync<AccountResponse>());
    }

    [Fact]
    public async Task Renommer_ou_desactiver_un_compte_ajoute_un_evenement()
    {
        var client = api.NewClient();
        await client.DefineAccountAsync(Cash, "Encaisse", "Debit").ExpectAsync(HttpStatusCode.Created);
        await client.DefineAccountAsync(Sales, "Ventes", "Credit").ExpectAsync(HttpStatusCode.Created);

        var renamed = await (await client.DefineAccountAsync(Cash, "Caisse principale", "Debit", isActive: false).ExpectAsync(HttpStatusCode.OK)).BodyAsync<AccountResponse>();
        var sales = await (await client.DefineAccountAsync(Sales, "Ventes", "Credit").ExpectAsync(HttpStatusCode.OK)).BodyAsync<AccountResponse>();

        Assert.Equal(3, renamed.ChartVersion);
        Assert.Equal(3, sales.ChartVersion);
        Assert.True(sales.SequenceNumber < renamed.SequenceNumber);
    }

    [Theory]
    [InlineData("USD", "Debit", false, "currency")]
    [InlineData("CAD", "Credit", false, "normalSide")]
    [InlineData("CAD", "Debit", true, "noNegativeBalance")]
    public async Task Les_attributs_qui_conditionnent_les_ecritures_sont_immuables(string currency, string normalSide, bool noNegativeBalance, string attribute)
    {
        var client = api.NewClient();
        await client.DefineAccountAsync(Cash, "Encaisse", "Debit").ExpectAsync(HttpStatusCode.Created);

        var problem = await client.DefineAccountAsync(Cash, "Encaisse", normalSide, noNegativeBalance, currency: currency)
            .DomainProblemAsync(HttpStatusCode.UnprocessableEntity, "AccountAttributeImmutable");

        Assert.Equal(attribute, problem.GetProperty("details").GetProperty("attribute").GetString());
    }

    [Fact]
    public async Task Un_nom_vide_est_une_regle_metier()
    {
        var client = api.NewClient();

        await client.DefineAccountAsync(Cash, "  ", "Debit").DomainProblemAsync(HttpStatusCode.UnprocessableEntity, "AccountInvalidName");
    }

    [Fact]
    public async Task Un_compte_desactive_refuse_les_nouvelles_lignes()
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();
        await client.DefineAccountAsync(Receivables, "Clients", "Debit", isActive: false).ExpectAsync(HttpStatusCode.OK);

        await client.PostAsync(Guid.CreateVersion7(), client.Entry(Line(Receivables, "Debit", 1m), Line(Sales, "Credit", 1m)))
            .DomainProblemAsync(HttpStatusCode.UnprocessableEntity, "EntryInactiveAccount");
    }
}
