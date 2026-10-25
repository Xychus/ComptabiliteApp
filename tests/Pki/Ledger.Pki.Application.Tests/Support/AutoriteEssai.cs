using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ledger.Pki.Application.Emission;
using Ledger.Pki.Infrastructure.Memoire;
using Ledger.SharedKernel;

namespace Ledger.Pki.Tests.Support;

/// <summary>Horloge réglable.</summary>
public sealed class HorlogeEssai(DateTimeOffset maintenant) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = maintenant;
}

/// <summary>
/// PKI d'essai : racine locale (clé en mémoire) et intermédiaire dont la clé est dans un
/// <see cref="SignataireMemoire"/>, comme la clé KMS en production.
/// </summary>
public sealed class AutoriteEssai : IDisposable
{
    public const string Cle = "arn:aws:kms:ca-central-1:111111111111:key/essai";

    public static readonly DateTimeOffset Maintenant = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private readonly ECDsa _cleRacine;

    public AutoriteEssai(TimeSpan? validiteIntermediaire = null, string nomRacine = "CN=Racine essai")
    {
        Horloge = new HorlogeEssai(Maintenant);
        Signataire = new SignataireMemoire();

        _cleRacine = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var demandeRacine = new CertificateRequest(nomRacine, _cleRacine, HashAlgorithmName.SHA384);
        demandeRacine.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 1, true));
        demandeRacine.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        demandeRacine.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(demandeRacine.PublicKey, false));
        Racine = demandeRacine.CreateSelfSigned(Maintenant.AddDays(-1), Maintenant.AddYears(10));

        var cleIntermediaire = Signataire.CreerCle(Cle);
        Intermediaire = CreerIntermediaire(cleIntermediaire, Maintenant + (validiteIntermediaire ?? TimeSpan.FromDays(365)), pathLength: 0);

        var autorite = AutoriteIntermediaire.Charger(Intermediaire.ExportCertificatePem(), Racine.ExportCertificatePem(), Cle);
        Autorite = autorite.IsSuccess ? autorite.Value : throw new InvalidOperationException(autorite.Error.Message);
    }

    public HorlogeEssai Horloge { get; }

    public SignataireMemoire Signataire { get; }

    public X509Certificate2 Racine { get; }

    public X509Certificate2 Intermediaire { get; }

    public AutoriteIntermediaire Autorite { get; }

    public EmetteurCertificats Emetteur(OptionsEmission? options = null) => new(Signataire, Autorite, Horloge, options ?? new OptionsEmission());

    public VerificateurCertificat Verificateur() => new(Autorite, Horloge);

    /// <summary>Certificat signé par la racine d'essai, avec une clé et des contraintes au choix (cas invalides).</summary>
    public X509Certificate2 CreerIntermediaire(ECDsa cle, DateTimeOffset fin, int? pathLength, bool autorite = true)
    {
        var demande = new CertificateRequest("CN=Intermediaire essai", cle, HashAlgorithmName.SHA384);
        demande.CertificateExtensions.Add(new X509BasicConstraintsExtension(autorite, pathLength is not null, pathLength ?? 0, true));
        demande.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        demande.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(demande.PublicKey, false));
        demande.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Racine, true, false));
        return demande.Create(Racine, Maintenant.AddDays(-1), fin, RandomNumberGenerator.GetBytes(8));
    }

    public void Dispose()
    {
        Autorite.Dispose();
        Intermediaire.Dispose();
        Racine.Dispose();
        Signataire.Dispose();
        _cleRacine.Dispose();
    }
}
