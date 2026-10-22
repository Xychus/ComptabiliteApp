using System.Net;
using Ledger.Contracts.Http.V1;
using Ledger.Query.Api.Composition;
using Ledger.Query.Api.Http;
using Ledger.Query.Api.Tests.Support;
using Ledger.Query.Tests.Support;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Ledger.Query.Api.Tests;

/// <summary>Composition : une configuration incomplète fait échouer le démarrage, pas la première requête.</summary>
public sealed class HebergementTests
{
    [Fact]
    public async Task Sans_autorite_Keycloak_l_application_ne_demarre_pas()
    {
        await using var fabrique = new FabriqueApiRequete(reglages: new Dictionary<string, string?> { ["Ledger:Authentication:Authority"] = string.Empty });

        Assert.Throws<OptionsValidationException>(() => fabrique.CreateClient());
    }

    [Fact]
    public async Task Sans_chaine_de_connexion_l_application_ne_demarre_pas()
    {
        await using var fabrique = new FabriqueApiRequete(reglages: new Dictionary<string, string?> { [ServicesRequete.CleFournisseur] = "Postgres" });

        var erreur = Assert.Throws<InvalidOperationException>(() => fabrique.CreateClient());
        Assert.Contains(ServicesRequete.NomChaineConnexion, erreur.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task La_lecture_en_memoire_est_refusee_hors_developpement()
    {
        await using var fabrique = new FabriqueApiRequete(environnement: "Production");

        Assert.Throws<InvalidOperationException>(() => fabrique.CreateClient());
    }

    [Theory]
    [InlineData("Development", false)]
    [InlineData("Production", true)]
    public async Task Les_metadonnees_OIDC_en_HTTP_ne_sont_tolerees_qu_en_developpement(string environnement, bool httpsExige)
    {
        // Keycloak local sans TLS (docker compose, ADR 0020) ; partout ailleurs, HTTPS exigé.
        await using var fabrique = new FabriqueApiRequete(chaineConnexion: "Host=localhost;Database=ledger;Username=ledger_query", environnement: environnement);

        var jwt = fabrique.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>().Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.Equal(httpsExige, jwt.RequireHttpsMetadata);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:05")]
    public async Task Un_seuil_de_fraicheur_nul_ou_negatif_empeche_le_demarrage(string seuil)
    {
        await using var fabrique = new FabriqueApiRequete(reglages: new Dictionary<string, string?> { ["Ledger:Fraicheur:RetardMaximal"] = seuil });

        var erreur = Assert.Throws<InvalidOperationException>(() => fabrique.CreateClient());
        Assert.Contains("RetardMaximal", erreur.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task La_sonde_de_disponibilite_echoue_si_la_base_est_injoignable()
    {
        await using var fabrique = new FabriqueApiRequete(chaineConnexion: "Host=127.0.0.1;Port=1;Database=ledger;Username=ledger_query;Timeout=1");
        var client = fabrique.CreateClient();

        var vivant = await client.LireAsync("/health/live");
        var disponible = await client.LireAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, vivant.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, disponible.StatusCode);
    }

    [Fact]
    public async Task Une_reponse_dont_les_projections_ont_un_evenement_en_retard_au_dela_du_seuil_est_perimee()
    {
        var maintenant = new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
        await using var fabrique = new FabriqueApiRequete(
            reglages: new Dictionary<string, string?> { ["Ledger:Fraicheur:RetardMaximal"] = "00:01:00" },
            horloge: new HorlogeFixe(maintenant));
        fabrique.Donnees.Modifier(t => t.AvecEvenement(1, maintenant.AddMinutes(-10)).AvecEvenement(2, maintenant.AddMinutes(-2)).AJourJusqua(1));

        var balance = await fabrique.CreerClientAuthentifie().LireAsync("/v1/trial-balance?entityId=ACME").CorpsAsync<TrialBalanceResponse>();

        Assert.Equal(new FreshnessDto(1, 2, true), balance.Freshness);
    }

    [Theory]
    [InlineData("AccountNotFound", 404)]
    [InlineData("JournalEntryNotFound", 404)]
    [InlineData("PeriodNotFound", 404)]
    [InlineData("InvalidCursor", 400)]
    [InlineData("CodeInconnu", 500)]
    public void Correspondance_code_statut_HTTP(string code, int statut) =>
        Assert.Equal(statut, ProblemesConsultation.StatutPour(code));

    private sealed class HorlogeFixe(DateTimeOffset maintenant) : SharedKernel.IClock
    {
        public DateTimeOffset UtcNow => maintenant;
    }
}
