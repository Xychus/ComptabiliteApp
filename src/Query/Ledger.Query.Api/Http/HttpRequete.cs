using System.Text.Json;
using System.Text.Json.Serialization;
using Ledger.Query.Application.Consultations;
using Ledger.SharedKernel;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ledger.Query.Api.Http;

/// <summary>Erreurs HTTP en Problem Details (RFC 9457) pour toutes les réponses d'erreur.</summary>
public static class HttpRequete
{
    public static IServiceCollection AjouterHttpRequete(this IServiceCollection services)
    {
        // Nombres JSON stricts (jamais de nombre en chaîne) : le contrat déclare des entiers, pas « entier ou chaîne ».
        services.ConfigureHttpJsonOptions(json => json.SerializerOptions.NumberHandling = JsonNumberHandling.Strict);
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.AddProblemDetails();
        services.AddExceptionHandler<GestionnaireRequeteInvalide>();
        return services;
    }
}

/// <summary>
/// Requête illisible (paramètre de requête mal formé, ex. <c>limit</c> non entier) : <c>400</c> en
/// Problem Details, avec la cause donnée par la liaison des paramètres.
/// </summary>
public sealed class GestionnaireRequeteInvalide(IProblemDetailsService problemes) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (exception is not BadHttpRequestException requeteInvalide)
        {
            return false;
        }

        httpContext.Response.StatusCode = requeteInvalide.StatusCode;
        return await problemes.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = requeteInvalide.StatusCode,
                Title = "Requête invalide",
                Detail = requeteInvalide.InnerException is JsonException json ? json.Message : requeteInvalide.Message,
            },
        }).ConfigureAwait(false);
    }
}

/// <summary>
/// Rejets des consultations en Problem Details, sur le modèle de l'API de commandes (ADR 0014) :
/// <c>type</c> = <c>urn:ledger:problem:{code}</c>, membres <c>code</c> et <c>details</c>. Ajouter
/// un code = ajouter une ligne à la table.
/// </summary>
public static class ProblemesConsultation
{
    public const string PrefixeType = "urn:ledger:problem:";

    private static readonly Dictionary<string, int> _statutParCode = new(StringComparer.Ordinal)
    {
        [ErreursConsultation.CompteIntrouvableCode] = StatusCodes.Status404NotFound,
        [ErreursConsultation.EcritureIntrouvableCode] = StatusCodes.Status404NotFound,
        [ErreursConsultation.PeriodeIntrouvableCode] = StatusCodes.Status404NotFound,
        [ErreursConsultation.CurseurInvalideCode] = StatusCodes.Status400BadRequest,
    };

    /// <summary>Statut d'un code ; un code inconnu est une erreur de programmation (<c>500</c>).</summary>
    public static int StatutPour(string code) => _statutParCode.GetValueOrDefault(code, StatusCodes.Status500InternalServerError);

    public static ProblemHttpResult VersProbleme(this DomainError erreur)
    {
        ArgumentNullException.ThrowIfNull(erreur);
        var statut = StatutPour(erreur.Code);
        return TypedResults.Problem(
            detail: erreur.Message,
            statusCode: statut,
            title: Titre(statut),
            type: PrefixeType + erreur.Code,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = erreur.Code,
                ["details"] = erreur.Details ?? new Dictionary<string, string>(StringComparer.Ordinal),
            });
    }

    private static string Titre(int statut) => statut switch
    {
        StatusCodes.Status404NotFound => "Ressource introuvable",
        StatusCodes.Status400BadRequest => "Requête invalide",
        _ => "Erreur interne",
    };
}
