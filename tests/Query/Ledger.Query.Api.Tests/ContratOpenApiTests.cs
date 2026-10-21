using System.Net;
using System.Text.Json.Nodes;
using Ledger.Query.Api.Tests.Support;

namespace Ledger.Query.Api.Tests;

/// <summary>
/// Le contrat versionné <c>docs/api/query-openapi.json</c> (généré au build) est exactement celui
/// que l'application sert : une modification de l'API sans régénération fait échouer ce test.
/// </summary>
[Collection(GroupeApiEnMemoire.Nom)]
public sealed class ContratOpenApiTests(ApiEnMemoire api)
{
    [Fact]
    public async Task Le_contrat_versionne_est_celui_que_l_application_sert()
    {
        var servi = await api.Fabrique.CreateClient().GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), AssertionsHttp.Ct);
        var versionne = await File.ReadAllTextAsync(Path.Join(RacineDepot(), "docs", "api", "query-openapi.json"), AssertionsHttp.Ct);

        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(versionne), JsonNode.Parse(servi)),
            "docs/api/query-openapi.json diffère du contrat servi : reconstruire Ledger.Query.Api et valider le fichier régénéré.");
    }

    [Fact]
    public async Task Les_parametres_entityId_et_period_sont_obligatoires_dans_le_contrat()
    {
        var contrat = JsonNode.Parse(await api.Fabrique.CreateClient().GetStringAsync(new Uri("/openapi/v1.json", UriKind.Relative), AssertionsHttp.Ct))!;

        var parametres = contrat["paths"]!.AsObject()
            .SelectMany(chemin => chemin.Value!.AsObject().SelectMany(operation => operation.Value!["parameters"]?.AsArray() ?? []))
            .Where(p => p!["in"]!.GetValue<string>() == "query" && p["name"]!.GetValue<string>() is "entityId" or "period")
            .ToList();

        Assert.NotEmpty(parametres);
        Assert.All(parametres, p => Assert.True(p!["required"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task Le_contrat_n_est_pas_servi_hors_developpement()
    {
        await using var production = new FabriqueApiRequete(chaineConnexion: "Host=localhost;Database=ledger;Username=ledger_query", environnement: "Production");

        var reponse = await production.CreateClient().LireAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.NotFound, reponse.StatusCode);
    }

    private static string RacineDepot()
    {
        for (var dossier = new DirectoryInfo(AppContext.BaseDirectory); dossier is not null; dossier = dossier.Parent)
        {
            if (File.Exists(Path.Join(dossier.FullName, "Ledger.slnx")))
            {
                return dossier.FullName;
            }
        }

        throw new InvalidOperationException("Racine du dépôt introuvable (Ledger.slnx).");
    }
}
