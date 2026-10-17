using System.Net;
using Ledger.Command.Api.Tests.Support;
using Ledger.Contracts.Http.V1;
using Ledger.Contracts.Integrity;
using static Ledger.Command.Api.Tests.Support.LedgerClient;

namespace Ledger.Command.Api.Tests.Postgres;

/// <summary>Critères d'acceptation de la phase 3 contre PostgreSQL, avec le rôle applicatif.</summary>
[Trait("Category", "Integration")]
public sealed class PostgresApiTests(PostgresApi database) : IClassFixture<PostgresApi>
{
    [Fact]
    public async Task Rejouer_deux_fois_le_meme_PUT_renvoie_le_meme_resultat_sans_doublon_en_base()
    {
        var client = await database.NewClient().WithChartAndOpenMarchAsync();
        var entryId = Guid.CreateVersion7();

        var first = await (await client.PostAsync(entryId, client.CashSale(114.98m)).ExpectAsync(HttpStatusCode.Created)).BodyAsync<JournalEntryResponse>();
        var second = await (await client.PostAsync(entryId, client.CashSale(114.98m)).ExpectAsync(HttpStatusCode.OK)).BodyAsync<JournalEntryResponse>();

        Assert.Equal(first, second);
        Assert.Equal(1, await database.CountAsync("SELECT count(*) FROM ledger.events WHERE stream_id = @stream", ("stream", EntryStream(entryId))));
        Assert.Equal(1, await database.CountAsync("SELECT count(*) FROM ledger.entry_ids WHERE entry_id = @id", ("id", entryId)));
        Assert.Equal(1, await database.CountAsync(
            "SELECT count(*) FROM ledger.events WHERE stream_id = @stream AND payload->>'sourceEntryId' = @id",
            ("stream", client.AccountStream(Cash)),
            ("id", entryId.ToString())));
        Assert.Equal(
            await database.CountAsync("SELECT count(*) FROM ledger.events WHERE entity_id = @entity", ("entity", client.Entity)),
            await database.CountAsync("SELECT count(*) FROM ledger.outbox WHERE partition_key = @entity", ("entity", client.Entity)));
    }

    [Fact]
    public async Task Une_ecriture_desequilibree_renvoie_422_et_rien_n_est_ecrit()
    {
        var client = await database.NewClient().WithChartAndOpenMarchAsync();
        var entryId = Guid.CreateVersion7();

        await client.PostAsync(entryId, client.Entry(Line(Cash, "Debit", 10m), Line(Sales, "Credit", 9m)))
            .DomainProblemAsync(HttpStatusCode.UnprocessableEntity, "EntryNotBalanced");

        Assert.Equal(0, await database.CountAsync("SELECT count(*) FROM ledger.events WHERE stream_id = @stream", ("stream", EntryStream(entryId))));
    }

    [Fact]
    public async Task Le_sceau_couvre_exactement_les_evenements_de_la_periode_en_base()
    {
        var client = await database.NewClient().WithChartAndOpenMarchAsync();
        var entryId = Guid.CreateVersion7();
        await client.PostAsync(entryId, client.CashSale(40m)).ExpectAsync(HttpStatusCode.Created);
        await client.ReverseAsync(entryId, Guid.CreateVersion7()).ExpectAsync(HttpStatusCode.Created);

        var seal = await (await client.SealAsync(March, expectedVersion: 1).ExpectAsync(HttpStatusCode.OK)).BodyAsync<PeriodResponse>();

        var hashes = new List<string>();
        await using (var command = database.Owner.CreateCommand(
            "SELECT hash FROM ledger.events WHERE entity_id = @entity AND period = @period AND event_type <> 'PeriodSealed' ORDER BY global_sequence"))
        {
            command.Parameters.AddWithValue("entity", client.Entity);
            command.Parameters.AddWithValue("period", March);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            while (await reader.ReadAsync(Ct))
            {
                hashes.Add(reader.GetString(0));
            }
        }

        Assert.Equal(5, hashes.Count);
        Assert.Equal(hashes.Count, seal.EventCount);
        Assert.Equal(MerkleTree.Root(hashes), seal.MerkleRoot);
        await client.PostAsync(Guid.CreateVersion7(), client.CashSale(1m)).DomainProblemAsync(HttpStatusCode.Conflict, "PeriodSealed");
    }

    [Fact]
    public async Task Le_plan_comptable_est_relu_depuis_le_journal()
    {
        var client = await database.NewClient().WithChartAndOpenMarchAsync();
        await client.DefineAccountAsync(Receivables, "Clients", "Debit", isActive: false).ExpectAsync(HttpStatusCode.OK);

        await client.PostAsync(Guid.CreateVersion7(), client.Entry(Line(Receivables, "Debit", 1m), Line(Sales, "Credit", 1m)))
            .DomainProblemAsync(HttpStatusCode.UnprocessableEntity, "EntryInactiveAccount");
        Assert.Equal(4, await database.CountAsync("SELECT count(*) FROM ledger.events WHERE stream_id = @stream", ("stream", client.ChartStream)));
    }

    [Fact]
    public async Task La_sonde_de_disponibilite_verifie_le_journal()
    {
        var response = await database.Factory.CreateClient().GetAsync(new Uri("/health/ready", UriKind.Relative), Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Un_appel_sans_jeton_renvoie_401()
    {
        var client = new LedgerClient(database.Factory.CreateClient());

        await (await client.PostAsync(Guid.CreateVersion7(), client.CashSale(1m))).ProblemAsync(HttpStatusCode.Unauthorized);
    }
}
