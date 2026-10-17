using System.Net;
using System.Text.Json.Nodes;
using Ledger.Command.Api.Tests.Support;

namespace Ledger.Command.Api.Tests;

/// <summary>
/// Le contrat versionné <c>docs/api/openapi.json</c> (généré au build) est exactement celui que
/// l'application sert : une modification de l'API sans régénération fait échouer ce test.
/// </summary>
[Collection(InMemoryApiGroup.Name)]
public sealed class OpenApiContractTests(InMemoryApi api)
{
    [Fact]
    public async Task Le_contrat_versionne_est_celui_que_l_application_sert()
    {
        var served = await api.Factory.CreateClient().GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);
        var versioned = await File.ReadAllTextAsync(Path.Join(RepositoryRoot(), "docs", "api", "openapi.json"), TestContext.Current.CancellationToken);

        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(versioned), JsonNode.Parse(served)),
            "docs/api/openapi.json diffère du contrat servi : reconstruire Ledger.Command.Api et valider le fichier régénéré.");
    }

    [Fact]
    public async Task Le_contrat_n_est_pas_servi_hors_developpement()
    {
        await using var production = new LedgerApiFactory(
            connectionString: "Host=localhost;Database=ledger;Username=ledger_app",
            environment: "Production");

        var response = await production.CreateClient().GetAsync(new Uri("/openapi/v1.json", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Join(directory.FullName, "Ledger.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Racine du dépôt introuvable (Ledger.slnx).");
    }
}
