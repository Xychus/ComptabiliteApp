using System.Text.Json;
using System.Text.Json.Serialization;
using Ledger.Command.Api.Endpoints;
using Microsoft.AspNetCore.Diagnostics;

namespace Ledger.Command.Api.Http;

/// <summary>Sérialisation JSON et erreurs HTTP en Problem Details (RFC 9457) pour toutes les réponses d'erreur.</summary>
public static class LedgerHttp
{
    public static IServiceCollection AddLedgerHttp(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(json =>
        {
            // Un champ obligatoire absent ou nul est une requête invalide (400), pas une valeur par défaut.
            // Les membres inconnus (dont les champs d'autorité serveur) restent ignorés.
            json.SerializerOptions.RespectNullableAnnotations = true;
            json.SerializerOptions.RespectRequiredConstructorParameters = true;
            json.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
        });
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        services.AddProblemDetails();
        services.AddExceptionHandler<BadRequestExceptionHandler>();
        services.AddSingleton<CommandLog>();
        return services;
    }
}

/// <summary>
/// Requête illisible (JSON invalide, champ obligatoire absent, paramètre de chemin mal formé) :
/// <c>400</c> en Problem Details, avec la cause donnée par le désérialiseur.
/// </summary>
public sealed class BadRequestExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (exception is not BadHttpRequestException badRequest)
        {
            return false;
        }

        httpContext.Response.StatusCode = badRequest.StatusCode;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = badRequest.StatusCode,
                Title = "Requête invalide",
                Detail = badRequest.InnerException is JsonException json ? json.Message : badRequest.Message,
            },
        }).ConfigureAwait(false);
    }
}
