using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ledger.Clients;

namespace Ledger.Authorizer.Tests;

/// <summary>PKI de test au format de la PKI interne (ADR 0010) : racine et intermédiaire P-384, certificats finaux P-256 au sujet <c>CN=nom</c>.</summary>
public sealed class PkiDeTest : IDisposable
{
    private static readonly DateTimeOffset Debut = DateTimeOffset.UtcNow.AddHours(-1);
    private readonly ECDsa _cleIntermediaire = ECDsa.Create(ECCurve.NamedCurves.nistP384);

    public PkiDeTest()
    {
        using var cleRacine = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var demandeRacine = new CertificateRequest("CN=Racine de test", cleRacine, HashAlgorithmName.SHA384);
        demandeRacine.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        demandeRacine.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        demandeRacine.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(demandeRacine.PublicKey, false));
        Racine = demandeRacine.CreateSelfSigned(Debut, Debut.AddYears(10));

        var demandeIntermediaire = new CertificateRequest("CN=Intermediaire de test", _cleIntermediaire, HashAlgorithmName.SHA384);
        demandeIntermediaire.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        demandeIntermediaire.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        demandeIntermediaire.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(demandeIntermediaire.PublicKey, false));
        demandeIntermediaire.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Racine, true, false));
        Intermediaire = demandeIntermediaire.Create(Racine, Debut, Debut.AddYears(1), [1]);
    }

    public X509Certificate2 Racine { get; }

    public X509Certificate2 Intermediaire { get; }

    public void Dispose()
    {
        _cleIntermediaire.Dispose();
        Racine.Dispose();
        Intermediaire.Dispose();
    }

    /// <summary>Certificat serveur : chaîne PEM (certificat puis intermédiaire) et clé PKCS#8.</summary>
    public (string Chaine, string Cle) Serveur(string nomDns)
    {
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var demande = Demande(nomDns, cle, "1.3.6.1.5.5.7.3.1");
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(nomDns);
        san.AddIpAddress(IPAddress.Loopback);
        demande.CertificateExtensions.Add(san.Build());
        using var certificat = Signer(demande);
        return ($"{certificat.ExportCertificatePem()}\n{Intermediaire.ExportCertificatePem()}", cle.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>Identité client, comme la lit une application depuis le secret de la PKI.</summary>
    public IdentiteClientPki Client(string nomCommun)
    {
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificat = Signer(Demande(nomCommun, cle, "1.3.6.1.5.5.7.3.2"));
        return IdentiteClientPki.DepuisPem($"{certificat.ExportCertificatePem()}\n{Intermediaire.ExportCertificatePem()}", cle.ExportPkcs8PrivateKeyPem());
    }

    private CertificateRequest Demande(string nomCommun, ECDsa cle, string usage)
    {
        var demande = new CertificateRequest($"CN={nomCommun}", cle, HashAlgorithmName.SHA384);
        demande.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        demande.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        demande.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(usage)], false));
        demande.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(demande.PublicKey, false));
        demande.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Intermediaire, true, false));
        return demande;
    }

    private X509Certificate2 Signer(CertificateRequest demande) =>
        demande.Create(Intermediaire.SubjectName, X509SignatureGenerator.CreateForECDsa(_cleIntermediaire), Debut, Debut.AddDays(7), RandomNumberGenerator.GetBytes(16));
}
