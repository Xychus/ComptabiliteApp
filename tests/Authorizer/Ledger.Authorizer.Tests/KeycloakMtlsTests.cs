using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Ledger.Clients;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Ledger.Authorizer.Tests;

/// <summary>
/// Keycloak de production (ADR 0030) : image officielle en mode <c>start</c>, HTTPS par la PKI,
/// TLS 1.3 seulement, mTLS demandé, realm AWS importé, aucun compte d'administration.
/// </summary>
public sealed class KeycloakDeProduction : IAsyncLifetime
{
    public const string Image = "quay.io/keycloak/keycloak:26.8";

    private const string RepertoireTls = "/opt/keycloak/conf/tls";

    public KeycloakDeProduction()
    {
        var (chaine, cle) = Pki.Serveur("localhost");
        Conteneur = new ContainerBuilder(Image)
            .WithCommand("start", "--import-realm")
            .WithEnvironment("KC_HTTP_ENABLED", "false")
            // Le test joint Keycloak par un port publié aléatoire : émetteur déduit de la requête.
            .WithEnvironment("KC_HOSTNAME_STRICT", "false")
            .WithEnvironment("KC_HTTPS_CERTIFICATE_FILE", $"{RepertoireTls}/serveur.pem")
            .WithEnvironment("KC_HTTPS_CERTIFICATE_KEY_FILE", $"{RepertoireTls}/serveur.key")
            .WithEnvironment("KC_HTTPS_PROTOCOLS", "TLSv1.3")
            .WithEnvironment("KC_HTTPS_CLIENT_AUTH", "request")
            .WithEnvironment("KC_HTTPS_TRUST_STORE_FILE", $"{RepertoireTls}/racine.pem")
            .WithEnvironment("KC_HEALTH_ENABLED", "true")
            .WithResourceMapping(Encoding.ASCII.GetBytes(chaine), $"{RepertoireTls}/serveur.pem")
            .WithResourceMapping(Encoding.ASCII.GetBytes(cle), $"{RepertoireTls}/serveur.key")
            .WithResourceMapping(Encoding.ASCII.GetBytes(Pki.Racine.ExportCertificatePem()), $"{RepertoireTls}/racine.pem")
            .WithResourceMapping(
                new FileInfo(Path.Join(AppContext.BaseDirectory, "infra", "keycloak", "ledger-realm.json")),
                new FileInfo("/opt/keycloak/data/import/ledger-realm.json"))
            .WithPortBinding(8443, true)
            .WithPortBinding(9000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(requete => requete
                .ForPort(9000)
                .ForPath("/health/ready")
                .UsingTls()
                .UsingHttpMessageHandler(new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator })))
            .Build();
    }

    public PkiDeTest Pki { get; } = new();

    public IContainer Conteneur { get; }

    public string Emetteur => $"https://localhost:{Conteneur.GetMappedPublicPort(8443)}/realms/ledger";

    public async ValueTask InitializeAsync() => await Conteneur.StartAsync().ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        await Conteneur.DisposeAsync().ConfigureAwait(false);
        Pki.Dispose();
    }
}

[Trait("Category", "Integration")]
public sealed class KeycloakMtlsTests(KeycloakDeProduction keycloak) : IClassFixture<KeycloakDeProduction>, IDisposable
{
    private readonly HttpClient _canalArriere = new(ConfianceRacinePki.CreerGestionnaire(keycloak.Pki.Racine.ExportCertificatePem())!);

    public void Dispose() => _canalArriere.Dispose();

    [Fact]
    public async Task Un_client_ecriture_obtient_en_mtls_un_jeton_es256_de_5_minutes_accepte_par_l_autorizer()
    {
        using var identite = keycloak.Pki.Client("ledger-exemple-ecriture");

        var jeton = await DemanderAsync("ledger-exemple-ecriture", identite);

        var jwt = new JsonWebToken(jeton);
        Assert.Equal("ES256", jwt.Alg);
        Assert.Equal(TimeSpan.FromMinutes(5), jwt.ValidTo - jwt.IssuedAt);
        var valide = await Validateur().ValiderAsync(jeton, TestContext.Current.CancellationToken);
        Assert.NotNull(valide);
        Assert.Equal("ledger-exemple-ecriture", valide.ClientId);
        Assert.Superset(new HashSet<string> { "ledger:read", "ledger:write" }, (HashSet<string>)valide.Portees);
    }

    [Fact]
    public async Task Un_client_en_lecture_seule_n_obtient_que_ledger_read()
    {
        using var identite = keycloak.Pki.Client("ledger-exemple-lecture");

        var valide = await Validateur().ValiderAsync(await DemanderAsync("ledger-exemple-lecture", identite), TestContext.Current.CancellationToken);

        Assert.NotNull(valide);
        Assert.Contains("ledger:read", valide.Portees);
        Assert.DoesNotContain("ledger:write", valide.Portees);
    }

    [Fact]
    public async Task Sans_certificat_client_aucun_jeton_n_est_emis()
    {
        await Assert.ThrowsAsync<EchecJetonException>(() => DemanderAsync("ledger-exemple-ecriture", identite: null));
    }

    [Fact]
    public async Task Le_certificat_d_un_autre_client_ne_donne_pas_de_jeton()
    {
        using var identite = keycloak.Pki.Client("ledger-exemple-lecture");

        await Assert.ThrowsAsync<EchecJetonException>(() => DemanderAsync("ledger-exemple-ecriture", identite));
    }

    [Fact]
    public async Task Un_certificat_d_une_autre_pki_ne_donne_pas_de_jeton()
    {
        using var autrePki = new PkiDeTest();
        using var identite = autrePki.Client("ledger-exemple-ecriture");

        // Selon le moment du refus : poignée de main TLS rompue ou client non authentifié.
        var erreur = await Record.ExceptionAsync(() => DemanderAsync("ledger-exemple-ecriture", identite));

        Assert.True(erreur is EchecJetonException or HttpRequestException, erreur?.ToString());
    }

    [Fact]
    public async Task Keycloak_demarre_sans_compte_d_administration()
    {
        var (sortie, erreurs) = await keycloak.Conteneur.GetLogsAsync(ct: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("admin user", sortie + erreurs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ledger", sortie + erreurs, StringComparison.Ordinal);
    }

    private async Task<string> DemanderAsync(string clientId, IdentiteClientPki? identite)
    {
        using var gestionnaire = new SocketsHttpHandler();
        if (identite is not null)
        {
            gestionnaire.SslOptions.ClientCertificateContext = identite.Contexte();
        }

        ConfianceRacinePki.Configurer(gestionnaire, keycloak.Pki.Racine);
        using var http = new HttpClient(gestionnaire);
        using var sansCertificat = identite is null ? keycloak.Pki.Client("sans-importance") : null;
        using var jetons = new FournisseurJetons(
            http,
            new OptionsClientGrandLivre
            {
                PointTerminaisonJetons = new Uri($"{keycloak.Emetteur}/protocol/openid-connect/token"),
                ClientId = clientId,
                IdentiteClient = identite ?? sansCertificat!,
                AdresseApi = new Uri("https://api.invalid/v1/"),
            },
            TimeProvider.System);
        return await jetons.ObtenirAsync(TestContext.Current.CancellationToken);
    }

    private ValidateurJetons Validateur()
    {
        var configuration = new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{keycloak.Emetteur}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(_canalArriere) { RequireHttps = true });
        return new ValidateurJetons(configuration, new OptionsAutorizer { Emetteur = keycloak.Emetteur, PrefixeClesPlanUsage = "/test" }, TimeProvider.System);
    }
}
