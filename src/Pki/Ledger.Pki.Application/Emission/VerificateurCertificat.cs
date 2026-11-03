using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ledger.Pki.Application.Profils;
using Ledger.SharedKernel;

namespace Ledger.Pki.Application.Emission;

/// <summary>
/// Vérifie un contenu émis avant sa promotion (étape <c>testSecret</c>) : clé et certificat
/// appariés, chaîne valide jusqu'à la racine attendue, identité conforme au profil, encore valide.
/// </summary>
public sealed class VerificateurCertificat(AutoriteIntermediaire autorite, IClock horloge)
{
    public Result<ContenuSecretCertificat> Verifier(ContenuSecretCertificat contenu, ProfilCertificat profil)
    {
        ArgumentNullException.ThrowIfNull(contenu);
        ArgumentNullException.ThrowIfNull(profil);

        X509Certificate2 certificat;
        X509Certificate2 feuille;
        try
        {
            certificat = X509Certificate2.CreateFromPem(contenu.Chaine, contenu.Cle);
            feuille = X509Certificate2.CreateFromPem(contenu.Certificat);
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            return Echec("pki.verification.appariement", $"Clé et certificat illisibles ou non appariés : {ex.Message}");
        }

        using (certificat)
        using (feuille)
        {
            if (!certificat.RawDataMemory.Span.SequenceEqual(feuille.RawDataMemory.Span))
            {
                return Echec("pki.verification.chaine", "Le premier certificat de la chaîne n'est pas le certificat final.");
            }

            if (!contenu.Racine.Contains(autorite.Racine.ExportCertificatePem(), StringComparison.Ordinal))
            {
                return Echec("pki.verification.racine", "La racine du secret n'est pas la racine attendue.");
            }

            using var chaine = new X509Chain();
            chaine.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chaine.ChainPolicy.CustomTrustStore.Add(autorite.Racine);
            chaine.ChainPolicy.ExtraStore.Add(autorite.Certificat);
            chaine.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chaine.ChainPolicy.VerificationTime = horloge.UtcNow.UtcDateTime;
            chaine.ChainPolicy.VerificationTimeIgnored = false;
            if (!chaine.Build(certificat))
            {
                var statuts = string.Join(", ", chaine.ChainStatus.Select(s => s.Status));
                return Echec("pki.verification.chaine", $"Chaîne invalide jusqu'à la racine : {statuts}.");
            }

            if (certificat.GetNameInfo(X509NameType.SimpleName, forIssuer: false) != profil.NomCommun)
            {
                return Echec("pki.verification.identite", "Le nom commun ne correspond pas au profil.");
            }

            var nomsDns = certificat.Extensions.OfType<X509SubjectAlternativeNameExtension>()
                .SelectMany(san => san.EnumerateDnsNames())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!nomsDns.SetEquals(profil.NomsDns))
            {
                return Echec("pki.verification.identite", "Les noms DNS ne correspondent pas au profil.");
            }
        }

        return Result.Success(contenu);
    }

    private static Result<ContenuSecretCertificat> Echec(string code, string message) =>
        Result.Failure<ContenuSecretCertificat>(new DomainError(code, message));
}
