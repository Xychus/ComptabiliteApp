using System.Net;
using Ledger.Command.Api.Tests.Support;
using Ledger.Contracts.Http.V1;
using static Ledger.Command.Api.Tests.Support.LedgerClient;

namespace Ledger.Command.Api.Tests;

/// <summary><c>POST /v1/journal-entries/{entryId}/reversals</c> : contre-passation idempotente.</summary>
[Collection(InMemoryApiGroup.Name)]
public sealed class ReversalEndpointTests(InMemoryApi api)
{
    [Fact]
    public async Task Une_contre_passation_cree_l_ecriture_inverse_et_retablit_le_solde_contraint()
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();
        var entryId = Guid.CreateVersion7();
        var reversalId = Guid.CreateVersion7();
        await client.PostAsync(entryId, client.CashSale(80m)).ExpectAsync(HttpStatusCode.Created);

        var response = await client.ReverseAsync(entryId, reversalId, description: "Annulation").ExpectAsync(HttpStatusCode.Created);

        var receipt = await response.BodyAsync<JournalEntryResponse>();
        Assert.Equal(reversalId, receipt.EntryId);
        Assert.Equal($"/v1/journal-entries/{reversalId}", response.Headers.Location?.OriginalString);
        var cash = await api.Store.ReadStreamAsync(client.AccountStream(Cash), Ct);
        Assert.Equal([80m, 0m], cash.Select(e => e.BalanceAfter));
    }

    [Fact]
    public async Task Rejouer_la_meme_contre_passation_renvoie_la_meme_reponse()
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();
        var entryId = Guid.CreateVersion7();
        var reversalId = Guid.CreateVersion7();
        await client.PostAsync(entryId, client.CashSale(10m)).ExpectAsync(HttpStatusCode.Created);
        var first = await (await client.ReverseAsync(entryId, reversalId).ExpectAsync(HttpStatusCode.Created)).BodyAsync<JournalEntryResponse>();

        var second = await (await client.ReverseAsync(entryId, reversalId).ExpectAsync(HttpStatusCode.OK)).BodyAsync<JournalEntryResponse>();

        Assert.Equal(first, second);
        Assert.Equal(2, (await api.Store.ReadStreamAsync(EntryStream(entryId), Ct)).Count);
    }

    [Fact]
    public async Task Une_seconde_contre_passation_est_un_conflit()
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();
        var entryId = Guid.CreateVersion7();
        await client.PostAsync(entryId, client.CashSale(10m)).ExpectAsync(HttpStatusCode.Created);
        await client.ReverseAsync(entryId, Guid.CreateVersion7()).ExpectAsync(HttpStatusCode.Created);

        await client.ReverseAsync(entryId, Guid.CreateVersion7()).DomainProblemAsync(HttpStatusCode.Conflict, "EntryAlreadyReversed");
    }

    [Fact]
    public async Task Rejouer_avec_une_autre_date_est_un_conflit_d_identifiant()
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();
        var entryId = Guid.CreateVersion7();
        var reversalId = Guid.CreateVersion7();
        await client.PostAsync(entryId, client.CashSale(10m)).ExpectAsync(HttpStatusCode.Created);
        await client.ReverseAsync(entryId, reversalId).ExpectAsync(HttpStatusCode.Created);

        await client.ReverseAsync(entryId, reversalId, new DateOnly(2026, 3, 16)).DomainProblemAsync(HttpStatusCode.Conflict, "EntryIdConflict");
    }

    [Fact]
    public async Task Contre_passer_une_ecriture_inconnue_renvoie_404()
    {
        var client = api.NewClient();

        await client.ReverseAsync(Guid.CreateVersion7(), Guid.CreateVersion7()).DomainProblemAsync(HttpStatusCode.NotFound, "EntryNotFound");
    }

    [Fact]
    public async Task L_identifiant_de_contre_passation_deja_utilise_est_un_conflit()
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();
        var entryId = Guid.CreateVersion7();
        var otherEntryId = Guid.CreateVersion7();
        await client.PostAsync(entryId, client.CashSale(10m)).ExpectAsync(HttpStatusCode.Created);
        await client.PostAsync(otherEntryId, client.CashSale(20m)).ExpectAsync(HttpStatusCode.Created);

        await client.ReverseAsync(entryId, otherEntryId).DomainProblemAsync(HttpStatusCode.Conflict, "EntryIdAlreadyUsed");
    }

    [Fact]
    public async Task Une_contre_passation_qui_rendrait_le_solde_contraint_negatif_renvoie_422()
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();
        var depositId = Guid.CreateVersion7();
        await client.PostAsync(depositId, client.CashSale(100m)).ExpectAsync(HttpStatusCode.Created);
        await client.PostAsync(Guid.CreateVersion7(), client.Entry(Line(Receivables, "Debit", 60m), Line(Cash, "Credit", 60m))).ExpectAsync(HttpStatusCode.Created);

        await client.ReverseAsync(depositId, Guid.CreateVersion7()).DomainProblemAsync(HttpStatusCode.UnprocessableEntity, "InsufficientBalance");
    }
}
