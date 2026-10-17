// Tâche ponctuelle : applique les migrations du journal (ADR 0007).
// La chaîne de connexion (rôle propriétaire, SSL Mode=VerifyFull en AWS) est injectée depuis
// Secrets Manager par la définition de tâche ; elle n'est jamais écrite dans le code ni l'image.
using Ledger.Migrations;

const string ConnectionStringVariable = "LEDGER_MIGRATIONS_CONNECTION_STRING";

var connectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
if (string.IsNullOrWhiteSpace(connectionString))
{
    await Console.Error.WriteLineAsync($"Variable {ConnectionStringVariable} absente.").ConfigureAwait(false);
    return 2;
}

var result = LedgerMigrator.Upgrade(connectionString);
if (!result.Successful)
{
    await Console.Error.WriteLineAsync($"Échec des migrations : {result.Error?.Message}").ConfigureAwait(false);
    return 1;
}

Console.WriteLine($"{result.ExecutedScripts.Count} script(s) appliqué(s).");
return 0;
