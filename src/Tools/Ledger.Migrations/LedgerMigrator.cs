using DbUp;

namespace Ledger.Migrations;

/// <summary>Résultat d'une exécution des migrations.</summary>
public sealed record MigrationResult(bool Successful, IReadOnlyList<string> ExecutedScripts, Exception? Error);

/// <summary>
/// Applique les scripts SQL intégrés (<c>Scripts/NNNN_*.sql</c>), chacun une seule fois et dans sa
/// propre transaction, avec le rôle propriétaire du schéma (ADR 0007). La substitution de
/// variables de DbUp est désactivée : les blocs PL/pgSQL utilisent <c>$$</c>.
/// </summary>
public static class LedgerMigrator
{
    public static MigrationResult Upgrade(string connectionString, bool logToConsole = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = DeployChanges.To.PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(LedgerMigrator).Assembly, name => name.EndsWith(".sql", StringComparison.Ordinal))
            .WithTransactionPerScript()
            .WithVariablesDisabled();
        builder = logToConsole ? builder.LogToConsole() : builder.LogToNowhere();

        var result = builder.Build().PerformUpgrade();
        return new MigrationResult(result.Successful, [.. result.Scripts.Select(script => script.Name)], result.Error);
    }
}
