using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ledger.Contracts.Http.V1;
using Ledger.EndToEnd.Tests.Support;

namespace Ledger.EndToEnd.Tests;

/// <summary>
/// Pannes du transport et du consommateur : PostgreSQL reste la source de vérité, Kafka n'est
/// qu'un transport (ADR 0007) ; les projections rattrapent sans perte ni double comptage (ADR 0015).
/// </summary>
[Collection(GroupeChaineCdc.Nom)]
[Trait("Category", "Integration")]
public sealed class ResilienceTests(ChaineCdc chaine)
{
    private static readonly DateOnly _date = new(2026, 3, 15);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Pendant_une_panne_de_Kafka_l_API_de_commandes_repond_201_puis_les_projections_rattrapent()
    {
        using var client = await chaine.NouveauClientAsync();
        var entite = "E2E-PANNE-" + Guid.CreateVersion7().ToString("N");
        await client.PreparerEntiteAsync(entite, "2026-03");

        await chaine.ArreterKafkaAsync();
        var statuts = new List<HttpStatusCode>();
        long derniere = 0;
        try
        {
            for (var i = 1; i <= 10; i++)
            {
                using var reponse = await client.EnvoyerEcritureAsync(Guid.CreateVersion7(), entite, _date, "1000", "4000", i);
                statuts.Add(reponse.StatusCode);
                derniere = Math.Max(derniere, await LireSequenceAsync(reponse));
            }
        }
        finally
        {
            await chaine.DemarrerKafkaAsync();
        }

        Assert.All(statuts, statut => Assert.Equal(HttpStatusCode.Created, statut));
        await chaine.AttendreEncaisseAsync(client, entite, derniere, 55m);
        await chaine.AttendreConsommationCompleteAsync();
        Assert.Equal(55m, (await client.LireEncaisseAsync(entite)).Solde);
    }

    [Fact]
    public async Task Un_redemarrage_du_worker_ne_compte_aucun_evenement_deux_fois()
    {
        using var client = await chaine.NouveauClientAsync();
        var entite = "E2E-REDEMARRAGE-" + Guid.CreateVersion7().ToString("N");
        await client.PreparerEntiteAsync(entite, "2026-03");
        await chaine.AttendreConsommationCompleteAsync();

        // Le worker est arrêté pendant les écritures, relancé, puis arrêté en plein rattrapage.
        await chaine.ArreterWorkerAsync();
        long derniere = 0;
        for (var i = 0; i < 100; i++)
        {
            derniere = (await client.PasserEcritureAsync(entite, _date, "1000", "4000", 1m)).SequenceNumber;
        }

        var avant = await chaine.MessagesConsommesAsync();
        await chaine.DemarrerWorkerAsync();
        await chaine.AttendreAsync(async () => await chaine.MessagesConsommesAsync() > avant, "reprise de la consommation");
        await chaine.ArreterWorkerAsync();
        await chaine.DemarrerWorkerAsync();

        await chaine.AttendreEncaisseAsync(client, entite, derniere, 100m);
        await chaine.AttendreConsommationCompleteAsync();
        Assert.Equal(100m, (await client.LireEncaisseAsync(entite)).Solde);
    }

    [Fact]
    public async Task Un_worker_qui_relit_tout_le_sujet_ne_modifie_aucun_solde()
    {
        using var client = await chaine.NouveauClientAsync();
        var entite = "E2E-RELECTURE-" + Guid.CreateVersion7().ToString("N");
        await client.PreparerEntiteAsync(entite, "2026-03");
        var vente = await client.PasserEcritureAsync(entite, _date, "1000", "4000", 7.25m);
        await chaine.AttendreEncaisseAsync(client, entite, vente.SequenceNumber, 7.25m);
        await chaine.AttendreConsommationCompleteAsync();
        var balanceAvant = await client.LireBalanceAsync(entite);

        // Relivraison de tout le sujet, comme après la perte des offsets Kafka.
        await chaine.ArreterWorkerAsync();
        await chaine.CompterAsync("WITH remis AS (UPDATE projections.consumer_offsets SET next_offset = 0 RETURNING 1) SELECT COUNT(*) FROM remis");
        await chaine.DemarrerWorkerAsync();
        await chaine.AttendreConsommationCompleteAsync();

        var balanceApres = await client.LireBalanceAsync(entite);
        Assert.Equal(7.25m, (await client.LireEncaisseAsync(entite)).Solde);
        Assert.Equal(balanceAvant.Lines, balanceApres.Lines);
    }

    private static async Task<long> LireSequenceAsync(HttpResponseMessage reponse)
    {
        if (reponse.StatusCode != HttpStatusCode.Created)
        {
            return 0;
        }

        var corps = await reponse.Content.ReadFromJsonAsync<JournalEntryResponse>(JsonSerializerOptions.Web, Ct);
        return corps!.SequenceNumber;
    }
}
