// Tâche ponctuelle : applique les migrations du journal (ADR 0007).
// La connexion (rôle propriétaire, SSL Mode=VerifyFull en AWS) vient de l'environnement : en AWS,
// l'utilisateur et le mot de passe sont injectés depuis Secrets Manager par la définition de tâche ;
// ils ne sont jamais écrits dans le code ni l'image (voir ChaineDeConnexion).
// « purger-outbox » : tâche planifiée de purge de l'outbox (rôle ledger_maintenance, voir PurgeOutbox).
using Ledger.Migrations;
using Ledger.Postgres.Infrastructure;

if (args is [PurgeOutbox.Commande])
{
    var purge = PurgeOutbox.Lire(Environment.GetEnvironmentVariable);
    if (purge.Chaine is null)
    {
        await Console.Error.WriteLineAsync($"Variables manquantes : {string.Join(", ", purge.VariablesManquantes)}.").ConfigureAwait(false);
        return 2;
    }

    IConfigurationSourceDonnees[] reglages = purge.Authentification.AuthentificationIam
        ? [new AuthentificationIamRds(Amazon.RegionEndpoint.GetBySystemName(purge.Authentification.Region))]
        : [];
    using var source = SourceDonneesPostgres.Creer(purge.Chaine, reglages);
    var supprimees = await PurgeOutbox.ExecuterAsync(source, purge.Retention, purge.Slot, CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine($"Outbox : {supprimees} ligne(s) purgée(s) (rétention {purge.Retention}, slot {purge.Slot}).");
    return 0;
}

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
