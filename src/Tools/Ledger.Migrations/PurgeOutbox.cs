using System.Globalization;
using Ledger.Postgres.Infrastructure;
using Npgsql;

namespace Ledger.Migrations;

/// <summary>Configuration de la purge, ou nom des variables d'environnement manquantes.</summary>
public sealed record ConfigurationPurge(string? Chaine, TimeSpan Retention, string Slot, OptionsPostgres Authentification, IReadOnlyList<string> VariablesManquantes);

/// <summary>
/// Tâche planifiée de purge de l'outbox (ADR 0020) : appelle <c>ledger.purger_outbox</c> avec le
/// rôle <c>ledger_maintenance</c>, seul autorisé. En AWS, connexion TLS VerifyFull par
/// authentification IAM du rôle de la tâche (ADR 0029) : aucun mot de passe.
/// </summary>
public static class PurgeOutbox
{
    public const string Commande = "purger-outbox";
    public const string VariableComplete = "LEDGER_PURGE_CONNECTION_STRING";
    public const string VariableHote = "LEDGER_PURGE_HOTE";
    public const string VariableBase = "LEDGER_PURGE_BASE";
    public const string VariableCertificatRacine = "LEDGER_PURGE_CERTIFICAT_RACINE";
    public const string VariableRegion = "LEDGER_PURGE_REGION";
    public const string VariableRetentionHeures = "LEDGER_PURGE_RETENTION_HEURES";
    public const string VariableSlot = "LEDGER_PURGE_SLOT";
    public const string Utilisateur = "ledger_maintenance";

    public static ConfigurationPurge Lire(Func<string, string?> lireVariable)
    {
        ArgumentNullException.ThrowIfNull(lireVariable);

        var retention = TimeSpan.FromHours(int.TryParse(lireVariable(VariableRetentionHeures), NumberStyles.None, CultureInfo.InvariantCulture, out var heures) ? heures : 168);
        var slot = lireVariable(VariableSlot) is { Length: > 0 } s ? s : "ledger_outbox";

        if (lireVariable(VariableComplete) is { Length: > 0 } complete)
        {
            return new ConfigurationPurge(complete, retention, slot, new OptionsPostgres(), []);
        }

        string[] requises = [VariableHote, VariableBase, VariableCertificatRacine, VariableRegion];
        var manquantes = requises.Where(v => string.IsNullOrWhiteSpace(lireVariable(v))).ToList();
        if (manquantes.Count > 0)
        {
            return new ConfigurationPurge(null, retention, slot, new OptionsPostgres(), manquantes);
        }

        var chaine = new NpgsqlConnectionStringBuilder
        {
            Host = lireVariable(VariableHote),
            Database = lireVariable(VariableBase),
            Username = Utilisateur,
            SslMode = SslMode.VerifyFull,
            RootCertificate = lireVariable(VariableCertificatRacine),
        }.ConnectionString;
        return new ConfigurationPurge(chaine, retention, slot, new OptionsPostgres { AuthentificationIam = true, Region = lireVariable(VariableRegion) }, []);
    }

    /// <summary>Supprime les lignes plus anciennes que la rétention ; renvoie le nombre de lignes supprimées.</summary>
    public static async Task<long> ExecuterAsync(NpgsqlDataSource source, TimeSpan retention, string slot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var commande = source.CreateCommand("SELECT ledger.purger_outbox($1, $2)");
        commande.Parameters.Add(new NpgsqlParameter { Value = retention });
        commande.Parameters.Add(new NpgsqlParameter { Value = slot });
        return (long)(await commande.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }
}
