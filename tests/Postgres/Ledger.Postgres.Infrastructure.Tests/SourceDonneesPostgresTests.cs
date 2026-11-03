using Amazon;
using Amazon.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ledger.Postgres.Infrastructure.Tests;

public sealed class SourceDonneesPostgresTests
{
    private const string ChaineAurora = "Host=ledger-dev.cluster-abc.ca-central-1.rds.amazonaws.com;Port=5432;Database=ledger;Username=ledger_app;SSL Mode=VerifyFull";

    [Fact]
    public void Creer_sans_reglage_conserve_la_chaine()
    {
        using var source = SourceDonneesPostgres.Creer("Host=localhost;Database=ledger;Username=ledger_app;Password=x", []);

        Assert.Equal("localhost", new NpgsqlConnectionStringBuilder(source.ConnectionString).Host);
    }

    [Fact]
    public void Creer_applique_les_reglages_enregistres()
    {
        var reglage = new ReglageEssai();
        using var fournisseur = new ServiceCollection().AddSingleton<IConfigurationSourceDonnees>(reglage).BuildServiceProvider();

        using var source = SourceDonneesPostgres.Creer(fournisseur, "Host=localhost;Database=ledger");

        Assert.Equal(1, reglage.Appels);
    }

    [Fact]
    public void AuthentificationIam_exige_verify_full()
    {
        var iam = new AuthentificationIamRds(RegionEndpoint.CACentral1, new BasicAWSCredentials("AKIAEXEMPLE", "secret"));

        Assert.Throws<InvalidOperationException>(() => iam.Configurer(new NpgsqlDataSourceBuilder("Host=h;Username=u;SSL Mode=Require")));
        using var source = SourceDonneesPostgres.Creer(ChaineAurora, [iam]);
        Assert.NotNull(source);
    }

    [Fact]
    public void AuthentificationIam_jeton_signe_pour_l_hote_le_port_et_l_utilisateur()
    {
        var iam = new AuthentificationIamRds(RegionEndpoint.CACentral1, new BasicAWSCredentials("AKIAEXEMPLE", "secret"));

        var jeton = iam.Jeton("ledger-dev.cluster-abc.ca-central-1.rds.amazonaws.com", 5432, "ledger_app");

        Assert.StartsWith("ledger-dev.cluster-abc.ca-central-1.rds.amazonaws.com:5432/?", jeton, StringComparison.Ordinal);
        Assert.Contains("Action=connect", jeton, StringComparison.Ordinal);
        Assert.Contains("DBUser=ledger_app", jeton, StringComparison.Ordinal);
        Assert.Contains("X-Amz-Signature=", jeton, StringComparison.Ordinal);
    }

    [Fact]
    public void AjouterAuthentificationPostgres_inactive_n_enregistre_rien() =>
        Assert.Empty(new ServiceCollection().AjouterAuthentificationPostgres(new OptionsPostgres()));

    [Fact]
    public void AjouterAuthentificationPostgres_active_exige_la_region() =>
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AjouterAuthentificationPostgres(new OptionsPostgres { AuthentificationIam = true }));

    [Fact]
    public void AjouterAuthentificationPostgres_active_enregistre_l_authentification_iam()
    {
        var services = new ServiceCollection().AjouterAuthentificationPostgres(new OptionsPostgres { AuthentificationIam = true, Region = "ca-central-1" });

        Assert.IsType<AuthentificationIamRds>(Assert.Single(services).ImplementationInstance);
    }

    private sealed class ReglageEssai : IConfigurationSourceDonnees
    {
        public int Appels { get; private set; }

        public void Configurer(NpgsqlDataSourceBuilder constructeur) => Appels++;
    }
}
