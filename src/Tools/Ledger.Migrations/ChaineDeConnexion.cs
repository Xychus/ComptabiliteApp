using Npgsql;

namespace Ledger.Migrations;

/// <summary>Chaîne de connexion lue, ou nom des variables d'environnement manquantes.</summary>
public sealed record LectureChaineDeConnexion(string? Chaine, IReadOnlyList<string> VariablesManquantes);

/// <summary>
/// Chaîne de connexion des migrations, lue dans l'environnement. Soit complète
/// (<see cref="VariableComplete"/>, développement local), soit composée à partir des variables
/// séparées de la définition de tâche ECS : hôte et base en clair, utilisateur et mot de passe
/// extraits du secret JSON géré par RDS, TLS en <c>VerifyFull</c> contre le certificat racine RDS
/// fourni dans l'image (CLAUDE.md, « Sécurité »).
/// </summary>
public static class ChaineDeConnexion
{
    public const string VariableComplete = "LEDGER_MIGRATIONS_CONNECTION_STRING";
    public const string VariableHote = "LEDGER_MIGRATIONS_HOTE";
    public const string VariableBase = "LEDGER_MIGRATIONS_BASE";
    public const string VariableUtilisateur = "LEDGER_MIGRATIONS_UTILISATEUR";
    public const string VariableMotDePasse = "LEDGER_MIGRATIONS_MOT_DE_PASSE";
    public const string VariableCertificatRacine = "LEDGER_MIGRATIONS_CERTIFICAT_RACINE";

    private static readonly string[] VariablesComposees =
        [VariableHote, VariableBase, VariableUtilisateur, VariableMotDePasse, VariableCertificatRacine];

    public static LectureChaineDeConnexion Lire(Func<string, string?> lireVariable)
    {
        ArgumentNullException.ThrowIfNull(lireVariable);

        var complete = lireVariable(VariableComplete);
        if (!string.IsNullOrWhiteSpace(complete))
        {
            return new LectureChaineDeConnexion(complete, []);
        }

        var valeurs = VariablesComposees.ToDictionary(nom => nom, lireVariable);
        var manquantes = valeurs.Where(v => string.IsNullOrWhiteSpace(v.Value)).Select(v => v.Key).ToList();
        if (manquantes.Count > 0)
        {
            return new LectureChaineDeConnexion(null, manquantes);
        }

        var constructeur = new NpgsqlConnectionStringBuilder
        {
            Host = valeurs[VariableHote],
            Database = valeurs[VariableBase],
            Username = valeurs[VariableUtilisateur],
            Password = valeurs[VariableMotDePasse],
            SslMode = SslMode.VerifyFull,
            RootCertificate = valeurs[VariableCertificatRacine],
        };
        return new LectureChaineDeConnexion(constructeur.ConnectionString, []);
    }
}
