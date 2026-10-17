using System.Net;
using Ledger.Command.Api.Tests.Support;
using Ledger.Contracts.Http.V1;
using Ledger.Contracts.Integrity;
using static Ledger.Command.Api.Tests.Support.LedgerClient;

namespace Ledger.Command.Api.Tests;

/// <summary>Ouverture et scellement des périodes avec version attendue (ADR 0013 et 0014).</summary>
[Collection(InMemoryApiGroup.Name)]
public sealed class PeriodEndpointTests(InMemoryApi api)
{
    [Fact]
    public async Task Ouvrir_une_periode_renvoie_sa_nouvelle_version()
    {
        var client = api.NewClient();

        var receipt = await (await client.OpenAsync(March, expectedVersion: 0).ExpectAsync(HttpStatusCode.OK)).BodyAsync<PeriodResponse>();

        Assert.Equal((client.Entity, March, "Open", 1L), (receipt.EntityId, receipt.Period, receipt.Status, receipt.Version));
        Assert.Null(receipt.EventCount);
        Assert.Null(receipt.MerkleRoot);
    }

    [Fact]
    public async Task Une_version_attendue_perimee_est_un_conflit()
    {
        var client = api.NewClient();
        await client.OpenAsync(March, expectedVersion: 0).ExpectAsync(HttpStatusCode.OK);

        var problem = await client.OpenAsync(March, expectedVersion: 0).DomainProblemAsync(HttpStatusCode.Conflict, "WrongExpectedVersion");

        Assert.Equal("0", problem.GetProperty("details").GetProperty("expectedVersion").GetString());
        Assert.Equal("1", problem.GetProperty("details").GetProperty("actualVersion").GetString());
    }

    [Fact]
    public async Task Rouvrir_une_periode_ouverte_est_un_conflit()
    {
        var client = api.NewClient();
        await client.OpenAsync(March, expectedVersion: 0).ExpectAsync(HttpStatusCode.OK);

        await client.OpenAsync(March, expectedVersion: 1).DomainProblemAsync(HttpStatusCode.Conflict, "PeriodAlreadyOpen");
    }

    [Fact]
    public async Task Sceller_publie_le_nombre_et_la_racine_de_Merkle_des_evenements_de_la_periode()
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();
        await client.PostAsync(Guid.CreateVersion7(), client.CashSale(10m)).ExpectAsync(HttpStatusCode.Created);
        await client.PostAsync(Guid.CreateVersion7(), client.Entry(Line(Receivables, "Debit", 5m), Line(Sales, "Credit", 5m))).ExpectAsync(HttpStatusCode.Created);
        var hashes = await api.Store.ReadPeriodHashesAsync(client.Entity, March, Ct);

        var receipt = await (await client.SealAsync(March, expectedVersion: 1).ExpectAsync(HttpStatusCode.OK)).BodyAsync<PeriodResponse>();

        Assert.Equal(("Sealed", 2L), (receipt.Status, receipt.Version));
        Assert.Equal(4, hashes.Count);
        Assert.Equal(hashes.Count, receipt.EventCount);
        Assert.Equal(MerkleTree.Root(hashes), receipt.MerkleRoot);
    }

    [Fact]
    public async Task Une_periode_scellee_refuse_les_ecritures_et_les_contre_passations()
    {
        var client = await api.NewClient().WithChartAndOpenMarchAsync();
        var entryId = Guid.CreateVersion7();
        await client.PostAsync(entryId, client.CashSale(10m)).ExpectAsync(HttpStatusCode.Created);
        await client.SealAsync(March, expectedVersion: 1).ExpectAsync(HttpStatusCode.OK);

        await client.PostAsync(Guid.CreateVersion7(), client.CashSale(10m)).DomainProblemAsync(HttpStatusCode.Conflict, "PeriodSealed");
        await client.ReverseAsync(entryId, Guid.CreateVersion7()).DomainProblemAsync(HttpStatusCode.Conflict, "PeriodSealed");
        await client.SealAsync(March, expectedVersion: 2).DomainProblemAsync(HttpStatusCode.Conflict, "PeriodAlreadySealed");
    }

    [Fact]
    public async Task Sceller_une_periode_jamais_ouverte_est_un_conflit()
    {
        var client = api.NewClient();

        await client.SealAsync(March, expectedVersion: 0).DomainProblemAsync(HttpStatusCode.Conflict, "PeriodNotOpen");
    }
}
