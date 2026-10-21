using Ledger.Query.Api.Securite;

namespace Ledger.Query.Api.Routes;

/// <summary>Réponses d'erreur communes aux consultations, déclarées dans le contrat OpenAPI.</summary>
public static class MetadonneesRoutes
{
    /// <summary><c>400</c> (paramètres), <c>401</c> et <c>403</c> (politique <see cref="AuthentificationRequete.PolitiqueLecture"/>), plus les statuts propres à la route.</summary>
    public static RouteHandlerBuilder ProduitProblemesLecture(this RouteHandlerBuilder route, params int[] statuts)
    {
        ArgumentNullException.ThrowIfNull(route);
        ArgumentNullException.ThrowIfNull(statuts);
        route.ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);
        foreach (var statut in statuts)
        {
            route.ProducesProblem(statut);
        }

        return route;
    }
}
