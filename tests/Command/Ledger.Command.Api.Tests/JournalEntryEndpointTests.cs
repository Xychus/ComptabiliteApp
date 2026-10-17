using System.Net;
using System.Net.Http.Json;
using System.Text;
using Ledger.Command.Api.Http;
using Ledger.Command.Api.Tests.Support;
using Ledger.Contracts.Http.V1;
using static Ledger.Command.Api.Tests.Support.LedgerClient;

namespace Ledger.Command.Api.Tests;

/// <summary><c>PUT /v1/journal-entries/{entryId}</c> : passation idempotente, rejets en Problem Details.</summary>
[Collection(InMemoryApiGroup.Name)]
public sealed class JournalEntryEndpointTests(InMemoryApi api)
{
    [Fact]
    public async Task Une_ecriture_creee_renvoie_201_avec_les_champs_serveur()
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();
        var entryId = Guid.CreateVersion7();

        var response = await client.PostAsync(entryId, client.CashSale(114.98m)).ExpectAsync(HttpStatusCode.Created);

        var receipt = await response.BodyAsync<JournalEntryResponse>();
        Assert.Equal(entryId, receipt.EntryId);
        Assert.Equal(March, receipt.Period);
        Assert.True(receipt.SequenceNumber > 0);
        Assert.Matches("^[0-9a-f]{64}$", receipt.Hash);
        Assert.Equal(TimeSpan.Zero, receipt.RecordedAt.Offset);
        Assert.Equal($"/v1/journal-entries/{entryId}", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Rejouer_le_meme_PUT_renvoie_le_meme_resultat_sans_doublon()
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();
        var entryId = Guid.CreateVersion7();
        var first = await (await client.PostAsync(entryId, client.CashSale(50m)).ExpectAsync(HttpStatusCode.Created)).BodyAsync<JournalEntryResponse>();

        var second = await (await client.PostAsync(entryId, client.CashSale(50m)).ExpectAsync(HttpStatusCode.OK)).BodyAsync<JournalEntryResponse>();

        Assert.Equal(first, second);
        Assert.Single(await api.Store.ReadStreamAsync(EntryStream(entryId), Ct));
        Assert.Single(await api.Store.ReadStreamAsync(client.AccountStream(Cash), Ct));
    }

    [Fact]
    public async Task Le_meme_identifiant_avec_un_autre_contenu_est_un_conflit()
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();
        var entryId = Guid.CreateVersion7();
        await client.PostAsync(entryId, client.CashSale(50m)).ExpectAsync(HttpStatusCode.Created);

        var problem = await client.PostAsync(entryId, client.CashSale(51m)).DomainProblemAsync(HttpStatusCode.Conflict, "EntryIdConflict");

        Assert.Equal(entryId.ToString(), problem.GetProperty("details").GetProperty("entryId").GetString());
    }

    [Fact]
    public async Task Une_ecriture_desequilibree_renvoie_422_designant_la_cause()
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();

        var problem = await client.PostAsync(Guid.CreateVersion7(), client.Entry(Line(Receivables, "Debit", 100m), Line(Sales, "Credit", 99.99m)))
            .DomainProblemAsync(HttpStatusCode.UnprocessableEntity, "EntryNotBalanced");

        var details = problem.GetProperty("details");
        Assert.Equal("CAD", details.GetProperty("currency").GetString());
        Assert.Equal("100.00", details.GetProperty("debits").GetString());
        Assert.Equal("99.99", details.GetProperty("credits").GetString());
        Assert.True(problem.TryGetProperty("traceId", out _));
    }

    [Theory]
    [InlineData("0", "EntryNonPositiveAmount")]
    [InlineData("-5", "EntryNonPositiveAmount")]
    [InlineData("1.005", "EntryInvalidAmountPrecision")]
    public async Task La_ligne_fautive_est_designee(string amount, string code)
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();
        var request = client.Entry(Line(Receivables, "Debit", 1m), new JournalLineDto(Sales, "Credit", new MoneyDto(amount, "CAD")));

        var problem = await client.PostAsync(Guid.CreateVersion7(), request).DomainProblemAsync(HttpStatusCode.UnprocessableEntity, code);

        Assert.Equal("1", problem.GetProperty("details").GetProperty("lineIndex").GetString());
        Assert.Equal(Sales, problem.GetProperty("details").GetProperty("account").GetString());
    }

    [Fact]
    public async Task Un_solde_contraint_insuffisant_renvoie_422()
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();
        await client.PostAsync(Guid.CreateVersion7(), client.CashSale(100m)).ExpectAsync(HttpStatusCode.Created);

        await client.PostAsync(Guid.CreateVersion7(), client.Entry(Line(Receivables, "Debit", 100.01m), Line(Cash, "Credit", 100.01m)))
            .DomainProblemAsync(HttpStatusCode.UnprocessableEntity, "InsufficientBalance");
    }

    [Fact]
    public async Task Une_ecriture_dans_une_periode_non_ouverte_est_un_conflit()
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();

        await client.PostAsync(Guid.CreateVersion7(), client.CashSale(10m) with { BusinessDate = new DateOnly(2026, 4, 1) })
            .DomainProblemAsync(HttpStatusCode.Conflict, "PeriodNotOpen");
    }

    [Fact]
    public async Task Un_compte_inconnu_renvoie_422()
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();

        var problem = await client.PostAsync(Guid.CreateVersion7(), client.Entry(Line("9999", "Debit", 10m), Line(Sales, "Credit", 10m)))
            .DomainProblemAsync(HttpStatusCode.UnprocessableEntity, "EntryInactiveAccount");

        Assert.Equal("0", problem.GetProperty("details").GetProperty("lineIndex").GetString());
    }

    [Fact]
    public async Task Les_champs_serveur_recus_en_entree_sont_ignores()
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();
        var entryId = Guid.CreateVersion7();
        var body = $$"""
            {
              "entityId": "{{client.Entity}}",
              "businessDate": "2026-03-15",
              "lines": [
                { "accountId": "1100", "side": "Debit", "amount": { "amount": "10.00", "currency": "CAD" } },
                { "accountId": "4000", "side": "Credit", "amount": { "amount": "10.00", "currency": "CAD" } }
              ],
              "sequenceNumber": 999999,
              "recordedAt": "1999-01-01T00:00:00Z",
              "period": "1999-01",
              "hash": "{{new string('0', 64)}}",
              "entryId": "{{Guid.CreateVersion7()}}"
            }
            """;

        var response = await client.Http.PutAsync(new Uri($"/v1/journal-entries/{entryId}", UriKind.Relative), new StringContent(body, Encoding.UTF8, "application/json"), Ct);

        var receipt = await (await Task.FromResult(response).ExpectAsync(HttpStatusCode.Created)).BodyAsync<JournalEntryResponse>();
        Assert.Equal(entryId, receipt.EntryId);
        Assert.Equal(March, receipt.Period);
        Assert.NotEqual(999999, receipt.SequenceNumber);
        Assert.NotEqual(new string('0', 64), receipt.Hash);
        Assert.True(receipt.RecordedAt.Year >= 2026);
    }

    [Fact]
    public async Task L_identifiant_de_correlation_fourni_est_repris_et_renvoye()
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();
        var entryId = Guid.CreateVersion7();
        var correlationId = Guid.CreateVersion7();
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/v1/journal-entries/{entryId}") { Content = JsonContent.Create(client.CashSale(5m)) };
        request.Headers.Add(CorrelationIdMiddleware.HeaderName, correlationId.ToString());

        var response = await client.Http.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(correlationId.ToString(), Assert.Single(response.Headers.GetValues(CorrelationIdMiddleware.HeaderName)));
        var stored = Assert.Single(await api.Store.ReadStreamAsync(EntryStream(entryId), Ct));
        Assert.Equal(correlationId, stored.Metadata.CorrelationId);
        Assert.Equal(TestTokens.Client, stored.Metadata.Actor);
    }

    [Fact]
    public async Task Sans_identifiant_de_correlation_un_UUIDv7_est_genere()
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();

        var response = await client.PostAsync(Guid.CreateVersion7(), client.CashSale(5m)).ExpectAsync(HttpStatusCode.Created);

        Assert.Equal(7, Guid.Parse(Assert.Single(response.Headers.GetValues(CorrelationIdMiddleware.HeaderName))).Version);
    }
}
