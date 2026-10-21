using System.Net;
using Ledger.Contracts.Http.V1;
using Ledger.Query.Api.Tests.Support;
using Ledger.Query.Tests.Support;
using static Ledger.Query.Tests.Support.JeuEssai;

namespace Ledger.Query.Api.Tests;

/// <summary>Solde, balance et sous-livre par HTTP : montants en chaînes, fraîcheur, erreurs en Problem Details.</summary>
[Collection(GroupeApiEnMemoire.Nom)]
public sealed class RoutesComptesTests(ApiEnMemoire api)
{
    private static readonly DateOnly _le10Mars = new(2026, 3, 10);
    private static readonly DateOnly _le20Mars = new(2026, 3, 20);

    [Fact]
    public async Task Le_solde_courant_est_renvoye_en_chaines_decimales_avec_la_fraicheur()
    {
        var entite = AmorcerVentes();

        var solde = await api.Client.LireAsync($"/v1/accounts/1000/balance?entityId={entite}").CorpsAsync<AccountBalanceResponse>();

        Assert.Equal((entite, "1000", "Encaisse", "Debit"), (solde.EntityId, solde.AccountId, solde.Name, solde.NormalSide));
        Assert.Null(solde.AsOf);
        Assert.Equal(new CurrencyBalanceDto("CAD", "114.98", "30.00", "84.98"), Assert.Single(solde.Balances));
        Assert.False(solde.Freshness.Stale);
    }

    [Fact]
    public async Task Le_solde_a_une_date_d_affaires_ignore_les_lignes_posterieures()
    {
        var entite = AmorcerVentes();

        var solde = await api.Client.LireAsync($"/v1/accounts/4000/balance?entityId={entite}&asOf=2026-03-15").CorpsAsync<AccountBalanceResponse>();

        Assert.Equal(new DateOnly(2026, 3, 15), solde.AsOf);
        Assert.Equal("100.00", Assert.Single(solde.Balances).Balance);
    }

    [Fact]
    public async Task Un_compte_inconnu_est_un_404_avec_le_code_de_la_cause()
    {
        var entite = AmorcerVentes();

        var probleme = await api.Client.LireAsync($"/v1/accounts/9999/balance?entityId={entite}").ProblemeAsync(HttpStatusCode.NotFound, "AccountNotFound");

        Assert.Equal("9999", probleme.GetProperty("details").GetProperty("accountId").GetString());
    }

    [Theory]
    [InlineData("/v1/accounts/1000/balance", "entityId")]
    [InlineData("/v1/accounts/1000/balance?entityId=a%20b", "entityId")]
    [InlineData("/v1/accounts/1000/balance?entityId=ACME&asOf=15-03-2026", "asOf")]
    [InlineData("/v1/accounts/1000/balance?entityId=ACME&asOf=2026-02-30", "asOf")]
    [InlineData("/v1/accounts/compte%20invalide/balance?entityId=ACME", "accountId")]
    [InlineData("/v1/trial-balance", "entityId")]
    [InlineData("/v1/trial-balance?entityId=ACME&asOf=demain", "asOf")]
    [InlineData("/v1/accounts/1000/entries", "entityId")]
    [InlineData("/v1/accounts/1000/entries?entityId=ACME&limit=0", "limit")]
    [InlineData("/v1/accounts/1000/entries?entityId=ACME&limit=501", "limit")]
    public async Task Un_parametre_mal_forme_est_un_400_qui_le_designe(string uri, string parametre) =>
        Assert.Equal([parametre], await api.Client.LireAsync(uri).ParametresInvalidesAsync());

    [Fact]
    public async Task Un_parametre_non_entier_est_un_400_en_problem_details() =>
        await api.Client.LireAsync("/v1/accounts/1000/entries?entityId=ACME&limit=beaucoup").ProblemeAsync(HttpStatusCode.BadRequest);

    [Fact]
    public async Task La_balance_courante_est_equilibree_et_totalisee_par_devise()
    {
        var entite = AmorcerVentes();

        var balance = await api.Client.LireAsync($"/v1/trial-balance?entityId={entite}").CorpsAsync<TrialBalanceResponse>();

        Assert.True(balance.Balanced);
        Assert.Equal(["1000", "2310", "2320", "4000", "5000"], balance.Lines.Select(l => l.AccountId));
        Assert.Equal(new TrialBalanceLineDto("1000", "Encaisse", "CAD", "114.98", "30.00", "84.98", "0.00"), balance.Lines[0]);
        Assert.Equal(new TrialBalanceTotalDto("CAD", "144.98", "144.98", "114.98", "114.98", true), Assert.Single(balance.Totals));
    }

    [Fact]
    public async Task La_balance_a_une_date_n_a_que_les_comptes_mouvementes_jusqu_a_cette_date()
    {
        var entite = AmorcerVentes();

        var balance = await api.Client.LireAsync($"/v1/trial-balance?entityId={entite}&asOf=2026-03-15").CorpsAsync<TrialBalanceResponse>();

        Assert.DoesNotContain(balance.Lines, l => l.AccountId == "5000");
        Assert.True(balance.Balanced);
    }

    [Fact]
    public async Task Le_sous_livre_se_parcourt_page_par_page_avec_le_curseur()
    {
        var entite = api.Amorcer(e =>
        {
            var jeu = new JeuEssai(e).Compte("1000", "Encaisse");
            for (var i = 1; i <= 3; i++)
            {
                jeu.Ecriture(Guid.CreateVersion7(), _le10Mars, i, [Debit("1000", i), Credit("4000", i)], $"Vente {i}");
            }

            return jeu;
        });

        var premiere = await api.Client.LireAsync($"/v1/accounts/1000/entries?entityId={entite}&limit=2").CorpsAsync<AccountEntriesResponse>();
        var seconde = await api.Client.LireAsync($"/v1/accounts/1000/entries?entityId={entite}&limit=2&cursor={premiere.NextCursor}").CorpsAsync<AccountEntriesResponse>();

        Assert.Equal(["Vente 1", "Vente 2"], premiere.Items.Select(l => l.Description));
        Assert.Equal(new MoneyDto("1.00", "CAD"), premiere.Items[0].Amount);
        Assert.Equal(("Debit", Mars, 1L), (premiere.Items[0].Side, premiere.Items[0].Period, premiere.Items[0].SequenceNumber));
        Assert.Equal(["Vente 3"], seconde.Items.Select(l => l.Description));
        Assert.Null(seconde.NextCursor);
    }

    [Fact]
    public async Task Un_curseur_illisible_est_un_400_InvalidCursor()
    {
        var entite = AmorcerVentes();

        await api.Client.LireAsync($"/v1/accounts/1000/entries?entityId={entite}&cursor=pas-un-curseur").ProblemeAsync(HttpStatusCode.BadRequest, "InvalidCursor");
    }

    [Fact]
    public async Task Le_sous_livre_d_un_compte_inconnu_est_un_404() =>
        await api.Client.LireAsync($"/v1/accounts/9999/entries?entityId={AmorcerVentes()}").ProblemeAsync(HttpStatusCode.NotFound, "AccountNotFound");

    /// <summary>Vente de 100 $ + TPS + TVQ le 10 mars, achat de 30 $ le 20 mars.</summary>
    private string AmorcerVentes() =>
        api.Amorcer(e => new JeuEssai(e)
            .Compte("1000", "Encaisse")
            .Compte("2310", "TPS à percevoir", "Credit")
            .Compte("2320", "TVQ à percevoir", "Credit")
            .Compte("4000", "Ventes", "Credit")
            .Compte("5000", "Achats")
            .Ecriture(Guid.CreateVersion7(), _le10Mars, 1, [Debit("1000", 114.98m), Credit("4000", 100m), Credit("2310", 5m), Credit("2320", 9.98m)])
            .Ecriture(Guid.CreateVersion7(), _le20Mars, 2, [Debit("5000", 30m), Credit("1000", 30m)]));
}
