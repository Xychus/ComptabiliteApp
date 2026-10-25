// Tâche ponctuelle : applique les migrations du journal (ADR 0007).
// La connexion (rôle propriétaire, SSL Mode=VerifyFull en AWS) vient de l'environnement : en AWS,
// l'utilisateur et le mot de passe sont injectés depuis Secrets Manager par la définition de tâche ;
// ils ne sont jamais écrits dans le code ni l'image (voir ChaineDeConnexion).
using Ledger.Migrations;

var lecture = ChaineDeConnexion.Lire(Environment.GetEnvironmentVariable);
if (lecture.Chaine is null)
{
    await Console.Error.WriteLineAsync(
        $"Variable {ChaineDeConnexion.VariableComplete} absente, et variables manquantes : {string.Join(", ", lecture.VariablesManquantes)}.").ConfigureAwait(false);
    return 2;
}

var result = LedgerMigrator.Upgrade(lecture.Chaine);
if (!result.Successful)
{
    await Console.Error.WriteLineAsync($"Échec des migrations : {result.Error?.Message}").ConfigureAwait(false);
    return 1;
}

Console.WriteLine($"{result.ExecutedScripts.Count} script(s) appliqué(s).");
return 0;
