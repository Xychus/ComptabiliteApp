namespace Ledger.Clients;

/// <summary>
/// Client HTTP prêt à l'emploi pour une application interne : nouvelles tentatives sur
/// <c>429</c>, puis jeton Keycloak (mTLS, en cache), puis API Gateway privée. Exemple :
/// <code>
/// using var grandLivre = ClientGrandLivre.Creer(options);
/// using var reponse = await grandLivre.Http.GetAsync("v1/trial-balance?entityId=…&amp;period=2026-11", annulation);
/// </code>
/// </summary>
public sealed class ClientGrandLivre : IDisposable
{
    private readonly HttpClient _keycloak;
    private readonly FournisseurJetons _jetons;

    private ClientGrandLivre(HttpClient keycloak, FournisseurJetons jetons, HttpClient http)
    {
        _keycloak = keycloak;
        _jetons = jetons;
        Http = http;
    }

    /// <summary>Client vers l'API, adresse de base = <see cref="OptionsClientGrandLivre.AdresseApi"/>.</summary>
    public HttpClient Http { get; }

    public static ClientGrandLivre Creer(OptionsClientGrandLivre options, TimeProvider? horloge = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Les gestionnaires appartiennent aux HttpClient (disposeHandler par défaut), libérés par Dispose.
#pragma warning disable CA2000
        var versKeycloak = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        versKeycloak.SslOptions.ClientCertificates = [options.CertificatClient];
        if (options.RacinePki is { } racine)
        {
            ConfianceRacinePki.Configurer(versKeycloak, racine);
        }

        var keycloak = new HttpClient(versKeycloak) { Timeout = TimeSpan.FromSeconds(10) };
        var jetons = new FournisseurJetons(keycloak, options, horloge ?? TimeProvider.System);

        // API Gateway présente un certificat public d'Amazon : magasin du système.
        var chaine = new GestionnaireReessai429(options)
        {
            InnerHandler = new GestionnaireJeton(jetons) { InnerHandler = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) } },
        };
#pragma warning restore CA2000
        return new ClientGrandLivre(keycloak, jetons, new HttpClient(chaine) { BaseAddress = options.AdresseApi });
    }

    public void Dispose()
    {
        Http.Dispose();
        _jetons.Dispose();
        _keycloak.Dispose();
    }
}
