using Npgsql;

namespace Ledger.Migrations.Tests;

/// <summary>Lecture de la chaîne de connexion des migrations dans l'environnement.</summary>
public sealed class ChaineDeConnexionTests
{
    private static readonly Dictionary<string, string> VariablesAws = new()
    {
        [ChaineDeConnexion.VariableHote] = "ledger-dev.cluster-abc.ca-central-1.rds.amazonaws.com",
        [ChaineDeConnexion.VariableBase] = "ledger",
        [ChaineDeConnexion.VariableUtilisateur] = "ledger_admin",
        [ChaineDeConnexion.VariableMotDePasse] = "m0t-de-passe",
        [ChaineDeConnexion.VariableCertificatRacine] = "/app/rds-ca-global.pem",
    };

    private static Func<string, string?> Environnement(Dictionary<string, string> variables) =>
        nom => variables.TryGetValue(nom, out var valeur) ? valeur : null;

    [Fact]
    public void La_chaine_complete_est_prioritaire()
    {
        var variables = new Dictionary<string, string>(VariablesAws)
        {
            [ChaineDeConnexion.VariableComplete] = "Host=postgres;Database=ledger;Username=postgres;Password=local",
        };

        var lecture = ChaineDeConnexion.Lire(Environnement(variables));

        Assert.Equal("Host=postgres;Database=ledger;Username=postgres;Password=local", lecture.Chaine);
        Assert.Empty(lecture.VariablesManquantes);
    }

    [Fact]
    public void Les_variables_separees_donnent_une_connexion_tls_verifiee()
    {
        var lecture = ChaineDeConnexion.Lire(Environnement(VariablesAws));

        Assert.NotNull(lecture.Chaine);
        var chaine = new NpgsqlConnectionStringBuilder(lecture.Chaine);
        Assert.Equal("ledger-dev.cluster-abc.ca-central-1.rds.amazonaws.com", chaine.Host);
        Assert.Equal("ledger", chaine.Database);
        Assert.Equal("ledger_admin", chaine.Username);
        Assert.Equal("m0t-de-passe", chaine.Password);
        Assert.Equal(SslMode.VerifyFull, chaine.SslMode);
        Assert.Equal("/app/rds-ca-global.pem", chaine.RootCertificate);
    }

    [Fact]
    public void Un_mot_de_passe_contenant_un_separateur_reste_intact()
    {
        var variables = new Dictionary<string, string>(VariablesAws) { [ChaineDeConnexion.VariableMotDePasse] = "a;b=c'd\"e" };

        var lecture = ChaineDeConnexion.Lire(Environnement(variables));

        Assert.Equal("a;b=c'd\"e", new NpgsqlConnectionStringBuilder(lecture.Chaine).Password);
    }

    [Fact]
    public void Les_variables_manquantes_sont_nommees()
    {
        var variables = new Dictionary<string, string>(VariablesAws);
        variables.Remove(ChaineDeConnexion.VariableMotDePasse);
        variables[ChaineDeConnexion.VariableCertificatRacine] = " ";

        var lecture = ChaineDeConnexion.Lire(Environnement(variables));

        Assert.Null(lecture.Chaine);
        Assert.Equal([ChaineDeConnexion.VariableMotDePasse, ChaineDeConnexion.VariableCertificatRacine], lecture.VariablesManquantes);
    }

    [Fact]
    public void Sans_aucune_variable_toutes_sont_manquantes()
    {
        var lecture = ChaineDeConnexion.Lire(_ => null);

        Assert.Null(lecture.Chaine);
        Assert.Equal(5, lecture.VariablesManquantes.Count);
    }
}
