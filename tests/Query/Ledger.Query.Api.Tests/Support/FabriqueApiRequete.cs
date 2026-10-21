using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ledger.Query.Api.Composition;
using Ledger.Query.Api.Securite;
using Ledger.Query.Infrastructure.Memoire;
using Ledger.Query.Tests.Support;
using Ledger.SharedKernel;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Ledger.Query.Api.Tests.Support;

/// <summary>
/// API de requête hébergée en mémoire. Les jetons sont validés contre la clé de
/// <see cref="JetonsEssai"/> (configuration OIDC fournie, aucune découverte réseau) ; la lecture
/// est en mémoire, ou PostgreSQL si une chaîne de connexion est fournie.
/// </summary>
public sealed class FabriqueApiRequete(
    string? chaineConnexion = null,
    string environnement = "Development",
    IReadOnlyDictionary<string, string?>? reglages = null,
    IClock? horloge = null)
    : WebApplicationFactory<Program>
{
    public DonneesLectureEnMemoire Donnees => Services.GetRequiredService<DonneesLectureEnMemoire>();

    public HttpClient CreerClientAuthentifie(string? jeton = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jeton ?? JetonsEssai.Valide());
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(environnement);
        builder.UseSetting(OptionsAuthentification.Section + ":Authority", JetonsEssai.Emetteur);
        if (chaineConnexion is null)
        {
            builder.UseSetting(ServicesRequete.CleFournisseur, ServicesRequete.FournisseurEnMemoire);
        }
        else
        {
            builder.UseSetting("ConnectionStrings:" + ServicesRequete.NomChaineConnexion, chaineConnexion);
        }

        foreach (var (cle, valeur) in reglages ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(cle, valeur);
        }

        builder.ConfigureLogging(journal => journal.ClearProviders());
        builder.ConfigureTestServices(services =>
        {
            if (horloge is not null)
            {
                services.AddSingleton(horloge);
            }

            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
            {
                // Remplace la découverte OIDC (créée par la post-configuration de JwtBearer) par une configuration fixe.
                var configuration = new OpenIdConnectConfiguration { Issuer = JetonsEssai.Emetteur };
                configuration.SigningKeys.Add(JetonsEssai.CleSignature);
                jwt.Configuration = configuration;
                jwt.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            });
        });
    }
}

/// <summary>Application partagée par les tests sans Docker : lecture en mémoire, une entité par test.</summary>
public sealed class ApiEnMemoire : IAsyncDisposable
{
    public FabriqueApiRequete Fabrique { get; } = new();

    public HttpClient Client => Fabrique.CreerClientAuthentifie();

    /// <summary>Entité propre à l'appelant, amorcée avec le jeu d'essai qu'il construit.</summary>
    public string Amorcer(Func<string, JeuEssai> jeu)
    {
        ArgumentNullException.ThrowIfNull(jeu);
        var entite = "API-" + Guid.CreateVersion7().ToString("N");
        Fabrique.Donnees.Modifier(jeu(entite).AppliquerA);
        return entite;
    }

    public ValueTask DisposeAsync() => Fabrique.DisposeAsync();
}

[CollectionDefinition(Nom)]
public sealed class GroupeApiEnMemoire : ICollectionFixture<ApiEnMemoire>
{
    public const string Nom = "api-requete-en-memoire";
}

public static class AssertionsHttp
{
    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<T> CorpsAsync<T>(this Task<HttpResponseMessage> enCours, HttpStatusCode attendu = HttpStatusCode.OK)
    {
        ArgumentNullException.ThrowIfNull(enCours);
        var reponse = await enCours;
        var corps = await reponse.Content.ReadAsStringAsync(Ct);
        Assert.True(reponse.StatusCode == attendu, $"Statut {(int)reponse.StatusCode} au lieu de {(int)attendu} : {corps}");
        return (await reponse.Content.ReadFromJsonAsync<T>(Ct))!;
    }

    /// <summary>Problem Details (RFC 9457) de la réponse, vérifié sur son type de contenu et son statut.</summary>
    public static async Task<JsonElement> ProblemeAsync(this Task<HttpResponseMessage> enCours, HttpStatusCode attendu)
    {
        ArgumentNullException.ThrowIfNull(enCours);
        var reponse = await enCours;
        var corps = await reponse.Content.ReadAsStringAsync(Ct);
        Assert.True(reponse.StatusCode == attendu, $"Statut {(int)reponse.StatusCode} au lieu de {(int)attendu} : {corps}");
        Assert.Equal("application/problem+json", reponse.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(corps);
        var probleme = document.RootElement.Clone();
        Assert.Equal((int)attendu, probleme.GetProperty("status").GetInt32());
        return probleme;
    }

    /// <summary>Problem Details d'un rejet : <c>type</c> et <c>code</c> désignent la cause.</summary>
    public static async Task<JsonElement> ProblemeAsync(this Task<HttpResponseMessage> enCours, HttpStatusCode attendu, string code)
    {
        var probleme = await enCours.ProblemeAsync(attendu);
        Assert.Equal(code, probleme.GetProperty("code").GetString());
        Assert.Equal("urn:ledger:problem:" + code, probleme.GetProperty("type").GetString());
        return probleme;
    }

    /// <summary>Paramètres en erreur d'un <c>400</c> de validation.</summary>
    public static async Task<IReadOnlyList<string>> ParametresInvalidesAsync(this Task<HttpResponseMessage> enCours)
    {
        // Comme l'API de commandes : type InvalidRequest et membre errors par paramètre, sans code de domaine.
        var probleme = await enCours.ProblemeAsync(HttpStatusCode.BadRequest);
        Assert.Equal("urn:ledger:problem:InvalidRequest", probleme.GetProperty("type").GetString());
        return [.. probleme.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)];
    }

    public static Task<HttpResponseMessage> LireAsync(this HttpClient client, string uri)
    {
        ArgumentNullException.ThrowIfNull(client);
        return client.GetAsync(new Uri(uri, UriKind.Relative), Ct);
    }
}
