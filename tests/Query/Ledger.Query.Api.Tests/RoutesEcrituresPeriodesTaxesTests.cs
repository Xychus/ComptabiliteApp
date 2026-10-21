using System.Net;
using Ledger.Contracts.Http.V1;
using Ledger.Query.Api.Tests.Support;
using Ledger.Query.Application.Lecture;
using Ledger.Query.Tests.Support;
using static Ledger.Query.Tests.Support.JeuEssai;

namespace Ledger.Query.Api.Tests;

/// <summary>Écriture, période et déclaration TPS/TVQ par HTTP.</summary>
[Collection(GroupeApiEnMemoire.Nom)]
public sealed class RoutesEcrituresPeriodesTaxesTests(ApiEnMemoire api)
{
    [Fact]
    public async Task Une_ecriture_contre_passee_est_renvoyee_avec_ses_lignes_et_ses_liens()
    {
        var vente = Guid.CreateVersion7();
        var inverse = Guid.CreateVersion7();
        var entite = api.Amorcer(e => new JeuEssai(e)
            .Ecriture(vente, new DateOnly(2026, 3, 10), 7, [Debit("1000", 10m), Credit("4000", 10m)], "Vente", contrePasseePar: inverse));

        var ecriture = await api.Client.LireAsync($"/v1/journal-entries/{vente}").CorpsAsync<JournalEntryDetailResponse>();

        Assert.Equal((vente, entite, Mars, "Vente", (Guid?)null, (Guid?)inverse, 7L, Origine.AddSeconds(7), Acteur), (ecriture.EntryId, ecriture.EntityId, ecriture.Period, ecriture.Description, ecriture.ReversalOf, ecriture.ReversedBy, ecriture.SequenceNumber, ecriture.RecordedAt, ecriture.Actor));
        Assert.Equal(
            [new JournalEntryLineDetailDto(1, "1000", "Debit", new MoneyDto("10.00", "CAD")), new JournalEntryLineDetailDto(2, "4000", "Credit", new MoneyDto("10.00", "CAD"))],
            ecriture.Lines);
    }

    [Fact]
    public async Task Une_ecriture_inconnue_est_un_404() =>
        await api.Client.LireAsync($"/v1/journal-entries/{Guid.CreateVersion7()}").ProblemeAsync(HttpStatusCode.NotFound, "JournalEntryNotFound");

    [Fact]
    public async Task Un_identifiant_d_ecriture_mal_forme_est_un_400() =>
        Assert.Equal(["entryId"], await api.Client.LireAsync("/v1/journal-entries/pas-un-uuid").ParametresInvalidesAsync());

    [Fact]
    public async Task Une_periode_scellee_est_renvoyee_avec_son_sceau_et_ses_mouvements()
    {
        var racine = new string('c', 64);
        var entite = api.Amorcer(e => new JeuEssai(e)
            .Periode(Mars, "Sealed", 2, Origine.AddDays(31), 4, racine)
            .Instantane(Mars, 12, 4, new CumulCompte("1000", "CAD", 10m, 0m), new CumulCompte("4000", "CAD", 0m, 10m)));

        var periode = await api.Client.LireAsync($"/v1/periods/{Mars}?entityId={entite}").CorpsAsync<PeriodDetailResponse>();

        Assert.Equal(("Sealed", 2L, (long?)4, racine), (periode.Status, periode.Version, periode.EventCount, periode.MerkleRoot));
        Assert.NotNull(periode.Snapshot);
        Assert.Equal((12L, 4L), (periode.Snapshot.SealSequenceNumber, periode.Snapshot.EventCount));
        Assert.Equal([new PeriodMovementDto("1000", "CAD", "10.00", "0.00"), new PeriodMovementDto("4000", "CAD", "0.00", "10.00")], periode.Snapshot.Movements);
    }

    [Fact]
    public async Task Une_periode_ouverte_n_a_pas_d_instantane()
    {
        var entite = api.Amorcer(e => new JeuEssai(e).Periode(Avril));

        var periode = await api.Client.LireAsync($"/v1/periods/{Avril}?entityId={entite}").CorpsAsync<PeriodDetailResponse>();

        Assert.Equal(("Open", 1L), (periode.Status, periode.Version));
        Assert.Null(periode.Snapshot);
        Assert.Null(periode.SealedAt);
    }

    [Fact]
    public async Task Une_periode_jamais_ouverte_est_un_404() =>
        await api.Client.LireAsync("/v1/periods/2026-05?entityId=ACME").ProblemeAsync(HttpStatusCode.NotFound, "PeriodNotFound");

    [Theory]
    [InlineData("/v1/periods/2026-13?entityId=ACME", "period")]
    [InlineData("/v1/periods/2026-03", "entityId")]
    [InlineData("/v1/tax/returns/gst-qst?entityId=ACME", "period")]
    [InlineData("/v1/tax/returns/gst-qst?period=2026-03", "entityId")]
    [InlineData("/v1/tax/returns/gst-qst?entityId=ACME&period=2026-3", "period")]
    public async Task Un_parametre_mal_forme_est_un_400_qui_le_designe(string uri, string parametre) =>
        Assert.Equal([parametre], await api.Client.LireAsync(uri).ParametresInvalidesAsync());

    /// <summary>Exemple de référence de l'ADR 0018.</summary>
    [Fact]
    public async Task La_declaration_TPS_TVQ_donne_les_montants_percus_payes_et_nets()
    {
        var entite = api.Amorcer(e => new JeuEssai(e).Periode(Mars, "Sealed", 2).TaxesPeriode(Mars, "CAD", 5m, 2.5m, 9.98m, 4.99m));

        var declaration = await api.Client.LireAsync($"/v1/tax/returns/gst-qst?entityId={entite}&period={Mars}").CorpsAsync<GstQstReturnResponse>();

        Assert.Equal((entite, Mars, "Sealed"), (declaration.EntityId, declaration.Period, declaration.PeriodStatus));
        Assert.Equal(new GstQstAmountsDto("CAD", "5.00", "2.50", "2.50", "9.98", "4.99", "4.99"), Assert.Single(declaration.Amounts));
    }

    [Fact]
    public async Task Une_periode_sans_taxe_donne_une_declaration_vide()
    {
        var declaration = await api.Client.LireAsync("/v1/tax/returns/gst-qst?entityId=SANS-TAXE&period=2026-05").CorpsAsync<GstQstReturnResponse>();

        Assert.Empty(declaration.Amounts);
        Assert.Null(declaration.PeriodStatus);
    }
}
