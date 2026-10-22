using System.Text.Json;

namespace Ledger.EndToEnd.Tests.Support;

/// <summary>
/// Configuration du connecteur Debezium telle que déployée (ADR 0020) : la partie invariante
/// (<c>deploy/debezium/ledger-outbox.json</c>) fusionnée avec les paramètres d'un environnement,
/// qui ne peuvent redéfinir aucune clé invariante (même règle que <c>enregistrer_connecteur.py</c>).
/// </summary>
public static class ConfigurationConnecteur
{
    public const string FichierInvariant = "deploy/debezium/ledger-outbox.json";

    public const string FichierLocal = "deploy/local/debezium-local.json";

    /// <summary>Clés qui dépendent de l'environnement : elles n'ont pas leur place dans le fichier invariant.</summary>
    public static IReadOnlyList<string> ClesEnvironnement { get; } =
        ["database.hostname", "database.port", "database.dbname", "database.user", "database.password", "database.sslmode", "database.sslrootcert"];

    public static IReadOnlyDictionary<string, string> Lire(string fichier)
    {
        var chemin = Path.Join(AppContext.BaseDirectory, fichier);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(chemin))
            ?? throw new InvalidOperationException($"Fichier vide : {fichier}.");
    }

    public static IReadOnlyDictionary<string, string> Fusionner(IReadOnlyDictionary<string, string> invariant, IReadOnlyDictionary<string, string> parametres)
    {
        ArgumentNullException.ThrowIfNull(invariant);
        ArgumentNullException.ThrowIfNull(parametres);
        var redefinies = parametres.Keys.Where(invariant.ContainsKey).Order(StringComparer.Ordinal).ToList();
        if (redefinies.Count > 0)
        {
            throw new InvalidOperationException($"Clés invariantes redéfinies par les paramètres : {string.Join(", ", redefinies)}.");
        }

        return invariant.Concat(parametres).ToDictionary(paire => paire.Key, paire => paire.Value, StringComparer.Ordinal);
    }
}
