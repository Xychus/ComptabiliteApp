using System.Reflection;

namespace Ledger.Command.Api.Composition;

/// <summary>
/// Génération du contrat OpenAPI au build : l'outil <c>GetDocument.Insider</c> démarre l'application
/// sans configuration d'exécution (autorité Keycloak, chaîne de connexion). Les contrôles de
/// configuration au démarrage sont alors suspendus ; ils restent actifs dans tout autre processus.
/// </summary>
public static class DocumentGeneration
{
    public static bool IsRunning { get; } = Assembly.GetEntryAssembly()?.GetName().Name == "GetDocument.Insider";
}
