using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Ledger.Clients;

/// <summary>
/// Appels sortants vers un service de la PKI interne (Keycloak, ADR 0030) : le certificat serveur
/// doit chaîner jusqu'à la racine de l'environnement, et à elle seule (ni les racines du système,
/// ni une autre PKI).
/// </summary>
public static class ConfianceRacinePki
{
    /// <summary>Gestionnaire HTTP qui n'admet que la racine <paramref name="racinePem"/> ; <c>null</c> sans racine (magasin du système).</summary>
    public static SocketsHttpHandler? CreerGestionnaire(string? racinePem)
    {
        if (string.IsNullOrWhiteSpace(racinePem))
        {
            return null;
        }

        var gestionnaire = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) };
        Configurer(gestionnaire, X509Certificate2.CreateFromPem(racinePem));
        return gestionnaire;
    }

    /// <summary>Restreint la validation du certificat serveur de <paramref name="gestionnaire"/> à <paramref name="racine"/>.</summary>
    public static void Configurer(SocketsHttpHandler gestionnaire, X509Certificate2 racine)
    {
        ArgumentNullException.ThrowIfNull(gestionnaire);
        ArgumentNullException.ThrowIfNull(racine);

        gestionnaire.SslOptions.RemoteCertificateValidationCallback = (_, certificat, chaineRecue, erreurs) => Valider(
            racine,
            certificat as X509Certificate2,
            chaineRecue?.ChainElements.Select(e => e.Certificate) ?? [],
            erreurs);
    }

    /// <summary>
    /// Vrai si le nom d'hôte correspond et si la chaîne aboutit à <paramref name="racine"/> par un
    /// magasin de confiance personnalisé. Les intermédiaires présentés servent seulement à la construire.
    /// </summary>
    public static bool Valider(X509Certificate2 racine, X509Certificate2? certificat, IEnumerable<X509Certificate2> intermediaires, SslPolicyErrors erreurs)
    {
        ArgumentNullException.ThrowIfNull(racine);
        ArgumentNullException.ThrowIfNull(intermediaires);
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
