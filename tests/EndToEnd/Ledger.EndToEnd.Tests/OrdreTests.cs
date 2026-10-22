using System.Net;
using Ledger.EndToEnd.Tests.Support;

namespace Ledger.EndToEnd.Tests;

/// <summary>
/// Ordre par flux : tous les événements d'une entité ont la même clé, donc la même partition, et
/// Debezium les publie dans l'ordre de validation ; le worker traite une partition dans l'ordre
/// de ses offsets. Les versions d'un flux sont donc publiées et appliquées dans l'ordre.
/// </summary>
[Collection(GroupeChaineCdc.Nom)]
[Trait("Category", "Integration")]
public sealed class OrdreTests(ChaineCdc chaine)
{
    private static readonly DateOnly _date = new(2026, 3, 20);

    [Fact]
    public async Task Les_evenements_d_un_meme_flux_sont_publies_et_projetes_dans_l_ordre()
    {
        using var client = await chaine.NouveauClientAsync();
        var entite = "E2E-ORDRE-" + Guid.CreateVersion7().ToString("N");
        await client.PreparerEntiteAsync(entite, "2026-03");

        // Écritures concurrentes sur le compte contraint (même flux account-…) : conflits de
        // version résolus par l'API, ou rejoués par le client avec le même identifiant (idempotent).
        var encaissements = await Task.WhenAll(Enumerable.Range(1, 24).Select(i => PasserAvecReprisesAsync(client, entite, "1000", "4000", 10m)));
        await Task.WhenAll(Enumerable.Range(1, 8).Select(_ => PasserAvecReprisesAsync(client, entite, "5000", "1000", 5m)));
        await client.ContrePasserAsync(encaissements[0], _date);
        await chaine.AttendreConsommationCompleteAsync();

        var messages = chaine.LireSujet().Where(m => m.Enveloppe.EntityId == entite).ToList();
        Assert.Single(messages.Select(m => m.Partition).Distinct());
        foreach (var flux in messages.GroupBy(m => m.Enveloppe.StreamId))
        {
            var versions = flux.OrderBy(m => m.Offset).Select(m => m.Enveloppe.StreamVersion).ToList();
            Assert.Equal(Enumerable.Range(1, versions.Count).Select(v => (long)v), versions);
        }

        // Le solde projeté est le balance_after du dernier événement du flux du compte contraint.
        var compte = messages.Where(m => m.Enveloppe.StreamId.StartsWith("account-", StringComparison.Ordinal)).MaxBy(m => m.Enveloppe.StreamVersion);
        Assert.NotNull(compte.Enveloppe);
        Assert.Equal(24 * 10m - 8 * 5m - 10m, decimal.Parse(compte.Enveloppe.BalanceAfter!, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(24 * 10m - 8 * 5m - 10m, (await client.LireEncaisseAsync(entite)).Solde);
    }

    private static async Task<Guid> PasserAvecReprisesAsync(ClientGrandLivre client, string entite, string debit, string credit, decimal montant)
    {
        var ecriture = Guid.CreateVersion7();
        for (var tentative = 1; ; tentative++)
        {
            using var reponse = await client.EnvoyerEcritureAsync(ecriture, entite, _date, debit, credit, montant);
            if (reponse.StatusCode is HttpStatusCode.Created or HttpStatusCode.OK)
            {
                return ecriture;
            }

            var corps = await reponse.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.True(reponse.StatusCode == HttpStatusCode.Conflict && tentative < 10, $"Statut {(int)reponse.StatusCode} : {corps}");
        }
    }
}
