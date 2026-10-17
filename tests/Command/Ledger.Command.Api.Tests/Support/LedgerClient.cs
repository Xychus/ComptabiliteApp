using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ledger.Command.Application.EventStore;
using Ledger.Contracts.Http.V1;
using Ledger.SharedKernel;

namespace Ledger.Command.Api.Tests.Support;

/// <summary>
/// Client HTTP de test : une entité propre au test (aucune interférence entre tests partageant
/// l'application), son plan comptable et des fabriques de requêtes.
/// </summary>
public sealed class LedgerClient(HttpClient http)
{
    public const string Cash = "1000";
    public const string Receivables = "1100";
    public const string Sales = "4000";
    public const string March = "2026-03";
    public static readonly DateOnly March15 = new(2026, 3, 15);

    public HttpClient Http { get; } = http;

    public string Entity { get; } = "API-" + Guid.CreateVersion7().ToString("N");

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Flux <c>account-{entité}-{compte}</c> d'un compte contraint, composants échappés (ADR 0004).</summary>
    public string AccountStream(string account) => StreamIds.Account(new EntityId(Entity), new AccountId(account));

    public string ChartStream => StreamIds.Chart(new EntityId(Entity));

    public static string EntryStream(Guid entryId) => StreamIds.Entry(new EntryId(entryId));

    /// <summary>Encaisse (contrainte), clients et ventes en CAD, puis mars ouvert.</summary>
    public async Task<LedgerClient> WithChartAndOpenMarchAsync()
    {
        await DefineAccountAsync(Cash, "Encaisse", "Debit", noNegativeBalance: true).ExpectAsync(HttpStatusCode.Created);
        await DefineAccountAsync(Receivables, "Clients", "Debit").ExpectAsync(HttpStatusCode.Created);
        await DefineAccountAsync(Sales, "Ventes", "Credit").ExpectAsync(HttpStatusCode.Created);
        await OpenAsync(March, expectedVersion: 0).ExpectAsync(HttpStatusCode.OK);
        return this;
    }

    public Task<HttpResponseMessage> DefineAccountAsync(string accountId, string name, string normalSide, bool noNegativeBalance = false, bool isActive = true, string currency = "CAD") =>
        Http.PutAsJsonAsync($"/v1/accounts/{accountId}", new DefineAccountRequest(Entity, name, currency, normalSide, isActive, noNegativeBalance), Ct);

    public Task<HttpResponseMessage> OpenAsync(string period, long expectedVersion) =>
        Http.PostAsJsonAsync($"/v1/periods/{period}/open", new PeriodCommandRequest(Entity, expectedVersion), Ct);

    public Task<HttpResponseMessage> SealAsync(string period, long expectedVersion) =>
        Http.PostAsJsonAsync($"/v1/periods/{period}/seal", new PeriodCommandRequest(Entity, expectedVersion), Ct);

    public Task<HttpResponseMessage> PostAsync(Guid entryId, PostJournalEntryRequest request) =>
        Http.PutAsJsonAsync($"/v1/journal-entries/{entryId}", request, Ct);

    public Task<HttpResponseMessage> ReverseAsync(Guid entryId, Guid reversalEntryId, DateOnly? businessDate = null, string? description = null) =>
        Http.PostAsJsonAsync($"/v1/journal-entries/{entryId}/reversals", new ReverseJournalEntryRequest(reversalEntryId, businessDate ?? March15, description), Ct);

    /// <summary>Vente de <paramref name="amount"/> encaissée : débit encaisse, crédit ventes.</summary>
    public PostJournalEntryRequest CashSale(decimal amount, string? description = "Vente comptoir") =>
        Entry(Line(Cash, "Debit", amount), Line(Sales, "Credit", amount)) with { Description = description };

    public PostJournalEntryRequest Entry(params JournalLineDto[] lines) => new(Entity, March15, lines);

    public static JournalLineDto Line(string account, string side, decimal amount, string currency = "CAD") =>
        new(account, side, new MoneyDto(amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), currency));
}

public static class HttpAssertions
{
    /// <summary>Vérifie le statut (avec le corps en cas d'écart) et renvoie la réponse.</summary>
    public static async Task<HttpResponseMessage> ExpectAsync(this Task<HttpResponseMessage> pending, HttpStatusCode expected)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var response = await pending;
        if (response.StatusCode != expected)
        {
            Assert.Fail($"Statut {(int)response.StatusCode} au lieu de {(int)expected} : {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        }

        return response;
    }

    public static async Task<T> BodyAsync<T>(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return (await response.Content.ReadFromJsonAsync<T>(TestContext.Current.CancellationToken))!;
    }

    /// <summary>Problem Details (RFC 9457) de la réponse, vérifié sur son type de contenu.</summary>
    public static async Task<JsonElement> ProblemAsync(this HttpResponseMessage response, HttpStatusCode expected)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.True(response.StatusCode == expected, $"Statut {(int)response.StatusCode} au lieu de {(int)expected} : {body}");
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(body);
        var problem = document.RootElement.Clone();
        Assert.Equal((int)expected, problem.GetProperty("status").GetInt32());
        return problem;
    }

    /// <summary>Problem Details d'un rejet de domaine : <c>type</c> et <c>code</c> désignent la cause.</summary>
    public static async Task<JsonElement> DomainProblemAsync(this Task<HttpResponseMessage> pending, HttpStatusCode expected, string code)
    {
        ArgumentNullException.ThrowIfNull(pending);
        var problem = await (await pending).ProblemAsync(expected);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal("urn:ledger:problem:" + code, problem.GetProperty("type").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
        return problem;
    }
}
