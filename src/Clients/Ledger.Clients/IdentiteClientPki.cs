using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Ledger.Clients;

/// <summary>
/// Identité TLS d'un client de la PKI interne : certificat <c>client</c> avec sa clé et les
/// intermédiaires présentés avec lui. Keycloak ne fait confiance qu'à la racine (ADR 0030) : sans
/// l'intermédiaire, la chaîne du client ne se construit pas et l'authentification échoue.
/// </summary>
public sealed class IdentiteClientPki : IDisposable
{
    public IdentiteClientPki(X509Certificate2 certificat, IReadOnlyList<X509Certificate2> intermediaires)
    {
        ArgumentNullException.ThrowIfNull(certificat);
        ArgumentNullException.ThrowIfNull(intermediaires);
        if (!certificat.HasPrivateKey)
        {
            throw new ArgumentException("Le certificat client doit porter sa clé privée.", nameof(certificat));
        }

        Certificat = certificat;
        Intermediaires = intermediaires;
    }

    public X509Certificate2 Certificat { get; }

    public IReadOnlyList<X509Certificate2> Intermediaires { get; }

    /// <summary>
    /// Depuis les champs <c>chaine</c> (certificat suivi de l'intermédiaire) et <c>cle</c> (PKCS#8)
    /// du secret de la PKI (ADR 0010).
    /// </summary>
    public static IdentiteClientPki DepuisPem(string chaine, string cle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chaine);
        ArgumentException.ThrowIfNullOrWhiteSpace(cle);

        var certificats = new X509Certificate2Collection();
        certificats.ImportFromPem(chaine);
        if (certificats.Count == 0)
        {
            throw new ArgumentException("Aucun certificat dans la chaîne.", nameof(chaine));
        }

        // Réimport PKCS#12 en mémoire : SChannel (Windows, développement) refuse une clé éphémère
        // issue d'un PEM pour s'authentifier ; sans effet sous Linux.
        using var depuisPem = X509Certificate2.CreateFromPem(certificats[0].ExportCertificatePem(), cle);
        var certificat = X509CertificateLoader.LoadPkcs12(depuisPem.Export(X509ContentType.Pkcs12), password: null);
        return new IdentiteClientPki(certificat, [.. certificats.Skip(1)]);
    }

    /// <summary>Contexte TLS qui présente le certificat et ses intermédiaires, sans les chercher en ligne.</summary>
    public SslStreamCertificateContext Contexte() =>
        SslStreamCertificateContext.Create(Certificat, [.. Intermediaires], offline: true);

    public void Dispose()
    {
        Certificat.Dispose();
        foreach (var intermediaire in Intermediaires)
        {
            intermediaire.Dispose();
        }
    }
}
