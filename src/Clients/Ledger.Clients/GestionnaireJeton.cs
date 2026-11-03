using System.Net;
using System.Net.Http.Headers;

namespace Ledger.Clients;

/// <summary>
/// Ajoute <c>Authorization: Bearer</c> à chaque requête. Un <c>401</c> (jeton refusé alors qu'il
/// semblait valide : clés du realm renouvelées) déclenche une seule nouvelle tentative avec un
/// nouveau jeton.
/// </summary>
public sealed class GestionnaireJeton : DelegatingHandler
{
    private readonly FournisseurJetons _jetons;

    public GestionnaireJeton(FournisseurJetons jetons)
    {
        ArgumentNullException.ThrowIfNull(jetons);
        _jetons = jetons;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Content is not null)
        {
            // Corps relu à l'éventuel second envoi.
            await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        }

        var jeton = await _jetons.ObtenirAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jeton);
        var reponse = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (reponse.StatusCode != HttpStatusCode.Unauthorized)
        {
            return reponse;
        }

        reponse.Dispose();
        _jetons.Invalider(jeton);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await _jetons.ObtenirAsync(cancellationToken).ConfigureAwait(false));
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
