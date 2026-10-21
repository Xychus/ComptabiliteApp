using System.Net;
using Ledger.Contracts.Http.V1;
using Ledger.Query.Api.Tests.Support;
using Ledger.Query.Infrastructure.Tests.Postgres;
using Ledger.Query.Tests.Support;
using static Ledger.Query.Tests.Support.JeuEssai;

namespace Ledger.Query.Api.Tests.Postgres;

/// <summary>API de requête sur un PostgreSQL éphémère migré, connectée avec le rôle <c>ledger_query</c>.</summary>
[Trait("Category", "Integration")]
[Collection(GroupePostgres.Nom)]
public sealed class ApiPostgresTests(PostgresLecture base_) : IAsyncLifetime
{
    private FabriqueApiRequete _fabrique = null!;

    public ValueTask InitializeAsync()
    {
        _fabrique = new FabriqueApiRequete(base_.ConnexionLecture);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Les_consultations_lisent_les_projections_avec_le_role_ledger_query()
    {
        var entite = "PG-" + Guid.CreateVersion7().ToString("N");
        var vente = Guid.CreateVersion7();
        await base_.AmorcerAsync(new JeuEssai(entite)
            .Compte("1000", "Encaisse")
            .Compte("4000", "Ventes", "Credit")
            .Periode(Mars)
            .Ecriture(vente, new DateOnly(2026, 3, 10), 1, [Debit("1000", 114.98m), Credit("4000", 114.98m)], "Vente")
            .TaxesPeriode(Mars, "CAD", 5m, 0m, 9.98m, 0m));
        var client = _fabrique.CreerClientAuthentifie();

        var solde = await client.LireAsync($"/v1/accounts/1000/balance?entityId={entite}").CorpsAsync<AccountBalanceResponse>();
        var balance = await client.LireAsync($"/v1/trial-balance?entityId={entite}&asOf=2026-03-31").CorpsAsync<TrialBalanceResponse>();
        var lignes = await client.LireAsync($"/v1/accounts/4000/entries?entityId={entite}").CorpsAsync<AccountEntriesResponse>();
        var ecriture = await client.LireAsync($"/v1/journal-entries/{vente}").CorpsAsync<JournalEntryDetailResponse>();
        var periode = await client.LireAsync($"/v1/periods/{Mars}?entityId={entite}").CorpsAsync<PeriodDetailResponse>();
        var taxes = await client.LireAsync($"/v1/tax/returns/gst-qst?entityId={entite}&period={Mars}").CorpsAsync<GstQstReturnResponse>();

        Assert.Equal("114.98", Assert.Single(solde.Balances).Balance);
        Assert.True(balance.Balanced);
        Assert.Equal("Vente", Assert.Single(lignes.Items).Description);
        Assert.Equal(2, ecriture.Lines.Count);
        Assert.Equal("Open", periode.Status);
        Assert.Equal("9.98", Assert.Single(taxes.Amounts).QstNet);
    }

    [Fact]
    public async Task La_sonde_de_disponibilite_repond_quand_la_base_repond()
    {
        var reponse = await _fabrique.CreateClient().LireAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, reponse.StatusCode);
    }

    public async ValueTask DisposeAsync() => await _fabrique.DisposeAsync();
}
