using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ledger.Command.Api.Composition;
using Ledger.Command.Api.Http;
using Ledger.Command.Api.Security;
using Ledger.Command.Api.Tests.Support;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ledger.Command.Api.Tests;

/// <summary>Composition : une configuration incomplète fait échouer le démarrage, pas la première requête.</summary>
public sealed class HostingTests
{
    [Fact]
    public void Sans_autorite_Keycloak_l_application_ne_demarre_pas()
    {
        // Validateur exécuté par l'hôte au démarrage (ValidateOnStart), appelé directement : un hôte
        // qui échoue au démarrage sous WebApplicationFactory lève tantôt l'erreur de validation, tantôt
        // ObjectDisposedException (course entre le fil de l'application et DeferredHost).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Ledger:Authentication:Authority"] = string.Empty })
            .Build();
        using var services = new ServiceCollection().AddLedgerAuthentication(configuration).BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());
        Assert.Contains(nameof(LedgerAuthenticationOptions.Authority), error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sans_chaine_de_connexion_l_application_ne_demarre_pas()
    {
        await using var factory = new LedgerApiFactory(settings: new Dictionary<string, string?> { [CommandServices.ProviderKey] = "Postgres" });

        var error = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
        Assert.Contains(CommandServices.ConnectionStringName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task L_event_store_en_memoire_est_refuse_hors_developpement()
    {
        await using var factory = new LedgerApiFactory(environment: "Production");

        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task La_racine_de_la_pki_seule_valide_le_canal_arriere_oidc()
    {
        // ADR 0030 : Keycloak présente un certificat de la PKI interne en AWS.
        using var cle = ECDsa.Create();
        using var racine = new CertificateRequest("CN=Racine", cle, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1));
        await using var avecRacine = new LedgerApiFactory(
            connectionString: "Host=localhost;Database=ledger;Username=ledger_app",
            settings: new Dictionary<string, string?> { ["Ledger:Tls:Racine"] = racine.ExportCertificatePem() });
        await using var sansRacine = new LedgerApiFactory(connectionString: "Host=localhost;Database=ledger;Username=ledger_app");

        var gestionnaire = Assert.IsType<SocketsHttpHandler>(avecRacine.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme).BackchannelHttpHandler);

        Assert.NotNull(gestionnaire.SslOptions.RemoteCertificateValidationCallback);
        Assert.Null(sansRacine.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme).BackchannelHttpHandler);
    }

    [Theory]
    [InlineData("Development", false)]
    [InlineData("Production", true)]
    public async Task Les_metadonnees_OIDC_en_HTTP_ne_sont_tolerees_qu_en_developpement(string environnement, bool httpsExige)
    {
        // Keycloak local sans TLS (docker compose, ADR 0020) ; partout ailleurs, HTTPS exigé.
        await using var factory = new LedgerApiFactory(connectionString: "Host=localhost;Database=ledger;Username=ledger_app", environment: environnement);

        var jwt = factory.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal(httpsExige, jwt.RequireHttpsMetadata);
    }

    [Fact]
    public async Task La_sonde_de_disponibilite_echoue_si_le_journal_est_injoignable()
    {
        await using var factory = new LedgerApiFactory(connectionString: "Host=127.0.0.1;Port=1;Database=ledger;Username=ledger_app;Timeout=1");
        var client = factory.CreateClient();

        var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative), TestContext.Current.CancellationToken);
        var ready = await client.GetAsync(new Uri("/health/ready", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    [Theory]
    [InlineData("EntryNotFound", 404)]
    [InlineData("ConcurrencyConflict", 409)]
    [InlineData("WrongExpectedVersion", 409)]
    [InlineData("PeriodSealed", 409)]
    [InlineData("EntryNotBalanced", 422)]
    [InlineData("CodeInconnu", 422)]
    public void Correspondance_code_de_domaine_statut_HTTP(string code, int status) =>
        Assert.Equal(status, DomainProblems.StatusFor(code));

    [Fact]
    public void La_generation_du_contrat_n_est_pas_detectee_dans_les_tests() =>
        Assert.False(DocumentGeneration.IsRunning);
}
