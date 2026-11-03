using System.Net;

namespace Ledger.Clients;

/// <summary>
/// Nouvelle tentative sur <c>429 Too Many Requests</c> (débit du plan d'usage dépassé, ADR 0031),
/// quelle que soit la méthode : API Gateway refuse la requête avant l'intégration, rien n'a été
/// exécuté. Délai : <c>Retry-After</c> s'il est présent, sinon recul exponentiel avec gigue
/// complète (tirage uniforme entre 0 et <c>initial × 2^n</c>), borné par le délai maximum.
/// </summary>
public sealed class GestionnaireReessai429 : DelegatingHandler
{
    private readonly OptionsClientGrandLivre _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _attendre;
    private readonly Func<double> _tirage;

    public GestionnaireReessai429(OptionsClientGrandLivre options)
        : this(options, Task.Delay, Random.Shared.NextDouble)
    {
    }

    internal GestionnaireReessai429(OptionsClientGrandLivre options, Func<TimeSpan, CancellationToken, Task> attendre, Func<double> tirage)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.TentativesMaximum, 1);
        _options = options;
        _attendre = attendre;
        _tirage = tirage;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Content is not null)
        {
            await request.Content.LoadIntoBufferAsync(cancellationToken).ConfigureAwait(false);
        }

        for (var tentative = 1; ; tentative++)
        {
            var reponse = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (reponse.StatusCode != HttpStatusCode.TooManyRequests || tentative >= _options.TentativesMaximum)
            {
                return reponse;
            }

            var delai = Delai(reponse, tentative);
            reponse.Dispose();
            await _attendre(delai, cancellationToken).ConfigureAwait(false);
        }
    }

    internal TimeSpan Delai(HttpResponseMessage reponse, int tentative)
    {
        var demande = reponse.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - (reponse.Headers.Date ?? DateTimeOffset.UtcNow),
            _ => (TimeSpan?)null,
        };
        if (demande is { } retryAfter)
        {
            return retryAfter < TimeSpan.Zero ? TimeSpan.Zero : Min(retryAfter, _options.DelaiMaximum);
        }

        var plafond = Min(_options.DelaiInitial * Math.Pow(2, tentative - 1), _options.DelaiMaximum);
        return plafond * _tirage();
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
}
