namespace Ledger.Authorizer;

/// <summary>
/// Routes accordées par chaque scope, exprimées en méthodes HTTP : le contrat sépare lectures
/// (<c>GET</c>, API de requête) et écritures (<c>PUT</c>, <c>POST</c>, API de commandes), comme le
/// routage de l'équilibreur (phase 8). Un nouveau scope s'ajoute ici, sans autre changement.
/// </summary>
public static class PolitiqueRoutes
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> MethodesParPortee =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["ledger:read"] = ["GET", "HEAD"],
            ["ledger:write"] = ["PUT", "POST"],
        };

    /// <summary>
    /// Ressources autorisées pour l'ensemble des scopes, sur toute l'étape de l'API. La politique
    /// est mise en cache par jeton puis réutilisée pour d'autres routes : elle doit donc couvrir
    /// toutes les routes permises, pas seulement celle de la requête qui l'a produite.
    /// </summary>
    public static IReadOnlyList<string> Ressources(string methodArn, IReadOnlySet<string> portees)
    {
        ArgumentNullException.ThrowIfNull(portees);

        var etape = BaseEtape(methodArn);
        return MethodesParPortee
            .Where(paire => portees.Contains(paire.Key))
            .SelectMany(paire => paire.Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(methode => $"{etape}/{methode}/*")
            .ToList();
    }

    /// <summary>Toute l'étape (<c>…:{api}/{étape}/*/*</c>) : utilisé pour un refus, qui s'applique aussi à toutes les routes.</summary>
    public static string ToutesLesRoutes(string methodArn) => $"{BaseEtape(methodArn)}/*/*";

    /// <summary><c>arn:aws:execute-api:{région}:{compte}:{api}/{étape}</c>, extrait de l'ARN de la méthode.</summary>
    internal static string BaseEtape(string methodArn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(methodArn);

        var segments = methodArn.Split('/');
        if (segments.Length < 3 || !segments[0].StartsWith("arn:aws:execute-api:", StringComparison.Ordinal) || segments[1].Length == 0)
        {
            throw new ArgumentException($"ARN de méthode inattendu : {methodArn}", nameof(methodArn));
        }

        return $"{segments[0]}/{segments[1]}";
    }
}
