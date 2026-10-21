namespace Ledger.Query.Api.Http;

/// <summary>
/// Identifiant de corrélation de la requête : repris de l'en-tête <c>X-Correlation-Id</c> s'il
/// contient un UUID, sinon un UUIDv7 est généré. Il est renvoyé dans la réponse et ajouté à la
/// portée de journalisation (l'API de requête n'écrit aucun événement).
/// </summary>
public sealed class IntergicielCorrelation(RequestDelegate suivant, ILogger<IntergicielCorrelation> journal)
{
    public const string NomEntete = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var correlation = Guid.TryParse(context.Request.Headers[NomEntete], out var fourni) ? fourni : Guid.CreateVersion7();
        context.Response.Headers[NomEntete] = correlation.ToString();
        using (journal.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlation }))
        {
            await suivant(context).ConfigureAwait(false);
        }
    }
}
