using System.Net;
using Ledger.Command.Api.Composition;
using Ledger.Command.Api.Http;
using Ledger.Command.Api.Tests.Support;
using Microsoft.Extensions.Options;

namespace Ledger.Command.Api.Tests;

/// <summary>Composition : une configuration incomplète fait échouer le démarrage, pas la première requête.</summary>
public sealed class HostingTests
{
    [Fact]
    public async Task Sans_autorite_Keycloak_l_application_ne_demarre_pas()
    {
        await using var factory = new LedgerApiFactory(settings: new Dictionary<string, string?> { ["Ledger:Authentication:Authority"] = string.Empty });

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
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
