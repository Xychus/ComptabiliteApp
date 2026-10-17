using Ledger.Command.Application.EventStore;
using Ledger.Command.Api.Security;

namespace Ledger.Command.Api.Http;

/// <summary>
/// Identifiant de corrélation de la requête (ADR 0014) : repris de l'en-tête <c>X-Correlation-Id</c>
/// s'il contient un UUID, sinon un UUIDv7 est généré. Il est renvoyé dans la réponse, ajouté à la
/// portée de journalisation et devient le <c>correlation_id</c> des événements.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var correlationId = Guid.TryParse(context.Request.Headers[HeaderName], out var provided) ? provided : Guid.CreateVersion7();
        context.Features.Set(new CorrelationIdFeature(correlationId));
        context.Response.Headers[HeaderName] = correlationId.ToString();
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context).ConfigureAwait(false);
        }
    }
}

/// <summary>Identifiant de corrélation de la requête courante.</summary>
public sealed record CorrelationIdFeature(Guid CorrelationId);

/// <summary>Métadonnées des événements produits par une requête : corrélation et client appelant.</summary>
public static class CommandMetadata
{
    public static EventMetadata From(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var correlation = context.Features.Get<CorrelationIdFeature>()
            ?? throw new InvalidOperationException($"{nameof(CorrelationIdMiddleware)} absent du pipeline.");
        return new EventMetadata(correlation.CorrelationId, CausationId: null, LedgerAuthentication.Actor(context.User));
    }
}
