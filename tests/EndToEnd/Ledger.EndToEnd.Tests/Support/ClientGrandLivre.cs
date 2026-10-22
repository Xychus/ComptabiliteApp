using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ledger.Contracts.Http.V1;

namespace Ledger.EndToEnd.Tests.Support;

/// <summary>Appels HTTP aux deux API, avec des jetons obtenus du Keycloak de test.</summary>
public sealed class ClientGrandLivre(HttpClient commandes, HttpClient requetes) : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public HttpClient Commandes => commandes;

    public HttpClient Requetes => requetes;

    public static ClientGrandLivre Creer(HttpClient commandes, HttpClient requetes, string jeton)
    {
        ArgumentNullException.ThrowIfNull(commandes);
        ArgumentNullException.ThrowIfNull(requetes);
        commandes.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jeton);
        requetes.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jeton);
        return new ClientGrandLivre(commandes, requetes);
    }

    public async Task DefinirCompteAsync(string entite, string compte, string nom, string sensNormal = "Debit", bool soldeNonNegatif = false)
    {
        var reponse = await commandes.PutAsJsonAsync(
            new Uri($"/v1/accounts/{compte}", UriKind.Relative),
            new DefineAccountRequest(entite, nom, "CAD", sensNormal, IsActive: true, soldeNonNegatif),
            Ct).ConfigureAwait(false);
        await VerifierAsync(reponse, HttpStatusCode.Created, HttpStatusCode.OK).ConfigureAwait(false);
    }

    public async Task OuvrirPeriodeAsync(string entite, string periode)
    {
        var reponse = await commandes.PostAsJsonAsync(
            new Uri($"/v1/periods/{periode}/open", UriKind.Relative),
            new PeriodCommandRequest(entite, ExpectedVersion: 0),
            Ct).ConfigureAwait(false);
        await VerifierAsync(reponse, HttpStatusCode.OK, HttpStatusCode.Created).ConfigureAwait(false);
    }

    /// <summary>Plan comptable minimal (encaisse contrainte, ventes) et période ouverte.</summary>
    public async Task PreparerEntiteAsync(string entite, string periode)
    {
        await DefinirCompteAsync(entite, "1000", "Encaisse", soldeNonNegatif: true).ConfigureAwait(false);
        await DefinirCompteAsync(entite, "4000", "Ventes", "Credit").ConfigureAwait(false);
        await DefinirCompteAsync(entite, "5000", "Achats").ConfigureAwait(false);
        await OuvrirPeriodeAsync(entite, periode).ConfigureAwait(false);
    }

    public Task<HttpResponseMessage> EnvoyerEcritureAsync(Guid ecriture, string entite, DateOnly date, string debit, string credit, decimal montant)
    {
        var montantDto = new MoneyDto(montant.ToString("0.00", CultureInfo.InvariantCulture), "CAD");
        return commandes.PutAsJsonAsync(
            new Uri($"/v1/journal-entries/{ecriture}", UriKind.Relative),
            new PostJournalEntryRequest(entite, date, [new JournalLineDto(debit, "Debit", montantDto), new JournalLineDto(credit, "Credit", montantDto)]),
            Ct);
    }

    /// <summary>Passe une écriture à deux lignes ; exige <c>201</c>.</summary>
    public async Task<JournalEntryResponse> PasserEcritureAsync(string entite, DateOnly date, string debit, string credit, decimal montant)
    {
        var reponse = await EnvoyerEcritureAsync(Guid.CreateVersion7(), entite, date, debit, credit, montant).ConfigureAwait(false);
        return await LireAsync<JournalEntryResponse>(reponse, HttpStatusCode.Created).ConfigureAwait(false);
    }

    public async Task<JournalEntryResponse> ContrePasserAsync(Guid ecriture, DateOnly date)
    {
        var reponse = await commandes.PostAsJsonAsync(
            new Uri($"/v1/journal-entries/{ecriture}/reversals", UriKind.Relative),
            new ReverseJournalEntryRequest(Guid.CreateVersion7(), date),
            Ct).ConfigureAwait(false);
        return await LireAsync<JournalEntryResponse>(reponse, HttpStatusCode.Created).ConfigureAwait(false);
    }

    public async Task<AccountBalanceResponse> LireSoldeAsync(string entite, string compte)
    {
        var reponse = await requetes.GetAsync(new Uri($"/v1/accounts/{compte}/balance?entityId={entite}", UriKind.Relative), Ct).ConfigureAwait(false);
        return await LireAsync<AccountBalanceResponse>(reponse, HttpStatusCode.OK).ConfigureAwait(false);
    }

    public async Task<TrialBalanceResponse> LireBalanceAsync(string entite)
    {
        var reponse = await requetes.GetAsync(new Uri($"/v1/trial-balance?entityId={entite}", UriKind.Relative), Ct).ConfigureAwait(false);
        return await LireAsync<TrialBalanceResponse>(reponse, HttpStatusCode.OK).ConfigureAwait(false);
    }

    /// <summary>
    /// Solde de l'encaisse (compte 1000) lu par l'API de requête et séquence vue par les
    /// projections ; zéro tant que le compte n'est pas encore projeté (<c>404</c>).
    /// </summary>
    public async Task<(decimal Solde, long VuJusqua)> LireEncaisseAsync(string entite)
    {
        using var reponse = await requetes.GetAsync(new Uri($"/v1/accounts/1000/balance?entityId={entite}", UriKind.Relative), Ct).ConfigureAwait(false);
        if (reponse.StatusCode == HttpStatusCode.NotFound)
        {
            return (0m, 0);
        }

        var solde = await LireAsync<AccountBalanceResponse>(reponse, HttpStatusCode.OK).ConfigureAwait(false);
        var montant = solde.Balances.Count == 0 ? 0m : decimal.Parse(solde.Balances.Single().Balance, CultureInfo.InvariantCulture);
        return (montant, solde.Freshness.ThroughSequence);
    }

    public void Dispose()
    {
        commandes.Dispose();
        requetes.Dispose();
    }

    private static async Task<T> LireAsync<T>(HttpResponseMessage reponse, HttpStatusCode attendu)
    {
        await VerifierAsync(reponse, attendu).ConfigureAwait(false);
        return (await reponse.Content.ReadFromJsonAsync<T>(JsonSerializerOptions.Web, Ct).ConfigureAwait(false))!;
    }

    private static async Task VerifierAsync(HttpResponseMessage reponse, params HttpStatusCode[] attendus)
    {
        if (!attendus.Contains(reponse.StatusCode))
        {
            var corps = await reponse.Content.ReadAsStringAsync(Ct).ConfigureAwait(false);
            Assert.Fail($"{reponse.RequestMessage?.Method} {reponse.RequestMessage?.RequestUri} : statut {(int)reponse.StatusCode} ({string.Join(" ou ", attendus.Select(s => (int)s))} attendu) : {corps}");
        }
    }
}
