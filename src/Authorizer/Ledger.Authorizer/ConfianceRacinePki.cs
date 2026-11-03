using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Ledger.Authorizer;

/// <summary>
/// Canal arrière vers Keycloak (configuration OIDC, JWKS) : le certificat serveur doit chaîner
/// jusqu'à la racine de la PKI de l'environnement, et à elle seule (ADR 0030). Sans racine
/// configurée, le magasin du système s'applique.
/// </summary>
public static class ConfianceRacinePki
{
    public static HttpMessageHandler CreerGestionnaire(string? racinePem)
    {
        var gestionnaire = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        if (racinePem is null)
        {
            return gestionnaire;
        }

        var racine = X509Certificate2.CreateFromPem(racinePem);
        gestionnaire.SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificat, chaineRecue, erreurs) => Valider(
                racine,
                certificat as X509Certificate2,
                chaineRecue?.ChainElements.Select(e => e.Certificate) ?? [],
                erreurs),
        };
        return gestionnaire;
    }

    /// <summary>
    /// Vrai si le nom d'hôte correspond et si la chaîne aboutit à <paramref name="racine"/> par
    /// un magasin de confiance personnalisé (aucune autre racine, ni celles du système). Les
    /// intermédiaires présentés par le serveur servent seulement à construire la chaîne.
    /// </summary>
    internal static bool Valider(X509Certificate2 racine, X509Certificate2? certificat, IEnumerable<X509Certificate2> intermediaires, SslPolicyErrors erreurs)
    {
        if (certificat is null || erreurs.HasFlag(SslPolicyErrors.RemoteCertificateNameMismatch) || erreurs.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))
        {
            return false;
        }

        using var chaine = new X509Chain();
        chaine.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chaine.ChainPolicy.CustomTrustStore.Add(racine);
        chaine.ChainPolicy.ExtraStore.AddRange(intermediaires.ToArray());
        // Certificats de 7 jours sans liste de révocation publiée (ADR 0010).
        chaine.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chaine.Build(certificat);
    }
}
