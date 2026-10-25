using Ledger.Pki.Infrastructure.Memoire;
using Ledger.Pki.Infrastructure.Postgres;
using Ledger.Pki.Infrastructure.Tests.Contrats;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ledger.Pki.Infrastructure.Tests.Postgres;

/// <summary>Administration des rôles contre un PostgreSQL réel (même version majeure qu'Aurora).</summary>
[Trait("Category", "Integration")]
public sealed class AdministrationRolesNpgsqlTests : IAsyncLifetime
{
    public const string Image = "postgres:18-alpine";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder(Image).Build();

    private static CancellationToken Jeton => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync(Jeton);
        await using var source = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        await using var commande = source.CreateCommand("CREATE ROLE debezium NOLOGIN; CREATE ROLE \"Rôle étrange\" NOLOGIN");
        await commande.ExecuteNonQueryAsync(Jeton);
    }

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    private AdministrationRolesNpgsql Administration()
    {
        var chaine = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString());
        return new AdministrationRolesNpgsql(
            new OptionsAdministrationPostgres(chaine.Host!, chaine.Database!, CertificatRacine: null, chaine.Port),
            new MaitreEssai(new IdentifiantsMaitre(chaine.Username!, chaine.Password!)));
    }

    [Fact]
    public async Task Le_role_se_connecte_avec_le_nouveau_mot_de_passe_seulement()
    {
        var administration = Administration();

        await administration.DefinirMotDePasseAsync("debezium", "premier-mot-de-passe", Jeton);
        await administration.DefinirMotDePasseAsync("debezium", "second-mot-de-passe", Jeton);

        Assert.True(await administration.TesterConnexionAsync("debezium", "second-mot-de-passe", Jeton));
        Assert.False(await administration.TesterConnexionAsync("debezium", "premier-mot-de-passe", Jeton));
    }

    [Fact]
    public async Task Le_mot_de_passe_est_stocke_sous_forme_de_verificateur_scram()
    {
        await Administration().DefinirMotDePasseAsync("debezium", "mot-de-passe", Jeton);

        await using var source = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        await using var commande = source.CreateCommand("SELECT rolpassword FROM pg_authid WHERE rolname = 'debezium'");
        var stocke = (string)(await commande.ExecuteScalarAsync(Jeton))!;
        Assert.StartsWith("SCRAM-SHA-256$4096:", stocke, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Le_nom_du_role_est_cite_par_le_serveur()
    {
        var administration = Administration();

        await administration.DefinirMotDePasseAsync("Rôle étrange", "mot-de-passe", Jeton);

        Assert.True(await administration.TesterConnexionAsync("Rôle étrange", "mot-de-passe", Jeton));
    }

    [Fact]
    public async Task IdentifiantsMaitreSecretsManager_lit_le_secret_gere_par_rds()
    {
        var memoire = new MagasinCertificatsMemoire();
        memoire.Creer("rds!cluster", new Dictionary<string, string>(StringComparer.Ordinal));
        memoire.AmorcerRotation("rds!cluster", "v1");
        await memoire.EcrireEnAttenteAsync("rds!cluster", "v1", """{"username":"postgres","password":"p"}""", Jeton);
        await memoire.PromouvoirAsync("rds!cluster", "v1", null, Jeton);
        using var client = new SecretsManagerEssai(memoire);

        var identifiants = await new IdentifiantsMaitreSecretsManager(client, "rds!cluster").LireAsync(Jeton);

        Assert.Equal(new IdentifiantsMaitre("postgres", "p"), identifiants);
        Assert.DoesNotContain("p }", identifiants.ToString(), StringComparison.Ordinal);
    }

    private sealed class MaitreEssai(IdentifiantsMaitre identifiants) : ISourceIdentifiantsMaitre
    {
        public Task<IdentifiantsMaitre> LireAsync(CancellationToken cancellationToken) => Task.FromResult(identifiants);
    }
}
