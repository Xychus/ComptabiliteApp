using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ledger.Pki.Application.Profils;
using Ledger.SharedKernel;

namespace Ledger.Pki.Application.Emission;

/// <summary>Durées d'émission des certificats finaux (ADR 0010).</summary>
public sealed class OptionsEmission
{
    /// <summary>Validité d'un certificat final.</summary>
    public TimeSpan Validite { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Antidatage de <c>notBefore</c>, contre les écarts d'horloge entre machines.</summary>
    public TimeSpan Antidatage { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Validité minimale acceptable quand l'échéance de l'intermédiaire écourte le certificat.</summary>
    public TimeSpan ValiditeMinimale { get; set; } = TimeSpan.FromDays(2);
}

/// <summary>
/// Émet un certificat final : nouvelle clé EC P-256 (acceptée par ACM, Java et librdkafka),
/// signature par la clé KMS de l'intermédiaire, échéance bornée par celle de l'intermédiaire.
/// </summary>
public sealed class EmetteurCertificats(ISignataireAutorite signataire, AutoriteIntermediaire autorite, IClock horloge, OptionsEmission options)
{
    private const string OidAuthentificationServeur = "1.3.6.1.5.5.7.3.1";
    private const string OidAuthentificationClient = "1.3.6.1.5.5.7.3.2";

    public async Task<Result<ContenuSecretCertificat>> EmettreAsync(ProfilCertificat profil, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profil);

        var maintenant = horloge.UtcNow;
        var debut = maintenant - options.Antidatage;
        var fin = maintenant + options.Validite;
        var finAutorite = new DateTimeOffset(autorite.Certificat.NotAfter.ToUniversalTime());
        if (fin > finAutorite)
        {
            fin = finAutorite;
        }

        if (fin - maintenant < options.ValiditeMinimale)
        {
            return Result.Failure<ContenuSecretCertificat>(new DomainError(
                "pki.emission.intermediaire-echu",
                $"L'intermédiaire expire le {finAutorite:O} : renouvelez-le (cérémonie annuelle) avant toute émission."));
        }

        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var demande = ConstruireDemande(profil, cle);
        var numeroSerie = NumeroSerie();

        using var certificat = await SignatureDifferee.ConstruireAsync(
            signataire,
            autorite.Cle,
            autorite.ClePublique,
            generateur => demande.Create(autorite.Certificat.SubjectName, generateur, debut, fin, numeroSerie),
            cancellationToken).ConfigureAwait(false);

        var certificatPem = certificat.ExportCertificatePem();
        return Result.Success(new ContenuSecretCertificat(
            Certificat: certificatPem,
            Chaine: $"{certificatPem}\n{autorite.Certificat.ExportCertificatePem()}",
            Cle: cle.ExportPkcs8PrivateKeyPem(),
            Racine: autorite.Racine.ExportCertificatePem(),
            ExpireLe: fin));
    }

    private CertificateRequest ConstruireDemande(ProfilCertificat profil, ECDsa cle)
    {
        var sujet = new X500DistinguishedNameBuilder();
        sujet.AddCommonName(profil.NomCommun);
        var demande = new CertificateRequest(sujet.Build(), cle, HashAlgorithmName.SHA384);

        demande.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        demande.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));

        var usages = new OidCollection();
        if (profil.Usages.HasFlag(UsageCertificat.Serveur))
        {
            usages.Add(new Oid(OidAuthentificationServeur));
        }

        if (profil.Usages.HasFlag(UsageCertificat.Client))
        {
            usages.Add(new Oid(OidAuthentificationClient));
        }

        demande.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, false));

        if (profil.NomsDns.Count > 0)
        {
            var san = new SubjectAlternativeNameBuilder();
            foreach (var nom in profil.NomsDns)
            {
                san.AddDnsName(nom);
            }

            demande.CertificateExtensions.Add(san.Build(critical: false));
        }

        demande.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(demande.PublicKey, false));
        demande.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(autorite.Certificat, true, false));
        return demande;
    }

    // RFC 5280 : entier positif d'au plus 20 octets ; 16 octets aléatoires, bit de poids fort à zéro.
    private static byte[] NumeroSerie()
    {
        var octets = RandomNumberGenerator.GetBytes(16);
        octets[0] &= 0x7F;
        octets[0] |= 0x01;
        return octets;
    }
}
