using System.Net;
using Ledger.EndToEnd.Tests.Support;

namespace Ledger.EndToEnd.Tests;

/// <summary>
/// <c>PUT</c> écriture → outbox → Debezium → Kafka → projection → <c>GET</c> solde, sans aucune
/// doublure : configuration Debezium et realm Keycloak réellement déployés (ADR 0020).
/// </summary>
[Collection(GroupeChaineCdc.Nom)]
[Trait("Category", "Integration")]
public sealed class ChaineCompleteTests(ChaineCdc chaine)
{
    private static readonly DateOnly _date = new(2026, 3, 10);

    [Fact]
    public async Task Une_ecriture_traverse_outbox_debezium_kafka_et_projection_jusqu_au_solde()
    {
        using var client = await chaine.NouveauClientAsync();
        var entite = NouvelleEntite();
        await client.PreparerEntiteAsync(entite, "2026-03");

        var vente = await client.PasserEcritureAsync(entite, _date, "1000", "4000", 114.98m);

        await chaine.AttendreEncaisseAsync(client, entite, vente.SequenceNumber, 114.98m);
        var ventes = await client.LireSoldeAsync(entite, "4000");
        var balance = await client.LireBalanceAsync(entite);
        Assert.Equal("114.98", Assert.Single(ventes.Balances).Balance);
        Assert.True(balance.Balanced);
        Assert.True(balance.Freshness.ThroughSequence >= vente.SequenceNumber);
    }

    [Fact]
    public async Task Les_evenements_ecrits_avant_le_connecteur_sont_publies_par_le_snapshot_initial()
    {
        using var client = await chaine.NouveauClientAsync();

        await chaine.AttendreAsync(
            async () => (await client.LireEncaisseAsync(ChaineCdc.EntiteAvantConnecteur)).Solde == ChaineCdc.MontantAvantConnecteur,
            "écriture antérieure au connecteur projetée");

        var publies = chaine.LireSujet().Where(m => m.Enveloppe.EntityId == ChaineCdc.EntiteAvantConnecteur).ToList();
        var journal = await chaine.CompterAsync("SELECT COUNT(*) FROM ledger.events WHERE entity_id = @entite", ("entite", ChaineCdc.EntiteAvantConnecteur));
        Assert.Equal(journal, publies.Select(m => m.Enveloppe.EventId).Distinct().LongCount());
    }

    [Fact]
    public async Task Le_message_publie_est_l_enveloppe_du_journal_avec_l_entite_pour_cle()
    {
        using var client = await chaine.NouveauClientAsync();
        var entite = NouvelleEntite();
        await client.PreparerEntiteAsync(entite, "2026-03");
        var vente = await client.PasserEcritureAsync(entite, _date, "1000", "4000", 10m);
        await chaine.AttendreEncaisseAsync(client, entite, vente.SequenceNumber, 10m);

        var message = Assert.Single(chaine.LireSujet(), m => m.Enveloppe.StreamId == $"entry-{vente.EntryId}");

        Assert.Equal(vente.SequenceNumber, message.Enveloppe.GlobalSequence);
        Assert.Equal(vente.Hash, message.Enveloppe.Hash);
        Assert.Equal(entite, message.Enveloppe.EntityId);
        Assert.Equal("JournalEntryPosted", message.Enveloppe.EventType);
    }

    [Fact]
    public async Task Le_realm_de_test_accorde_la_lecture_sans_l_ecriture_au_client_de_lecture()
    {
        using var lecteur = await chaine.NouveauClientAsync(ChaineCdc.ClientLecture);

        var lecture = await lecteur.Requetes.GetAsync(new Uri($"/v1/trial-balance?entityId={ChaineCdc.EntiteAvantConnecteur}", UriKind.Relative), TestContext.Current.CancellationToken);
        var ecriture = await lecteur.EnvoyerEcritureAsync(Guid.CreateVersion7(), ChaineCdc.EntiteAvantConnecteur, _date, "1000", "4000", 1m);

        Assert.Equal(HttpStatusCode.OK, lecture.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, ecriture.StatusCode);
    }

    private static string NouvelleEntite() => "E2E-" + Guid.CreateVersion7().ToString("N");
}
