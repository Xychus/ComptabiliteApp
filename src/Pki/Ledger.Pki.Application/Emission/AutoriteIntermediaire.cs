using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ledger.SharedKernel;

namespace Ledger.Pki.Application.Emission;

/// <summary>
/// CA intermédiaire active (ADR 0010) : son certificat, signé hors ligne par la racine, et la clé
/// KMS qui détient sa clé privée. Les deux certificats sont publics et versionnés dans Git
/// (<c>infra/envs/&lt;env&gt;/pki/</c>).
/// </summary>
public sealed class AutoriteIntermediaire : IDisposable
{
    private AutoriteIntermediaire(X509Certificate2 certificat, X509Certificate2 racine, string cle, ECDsa clePublique)
    {
        Certificat = certificat;
        Racine = racine;
        Cle = cle;
        ClePublique = clePublique;
    }

    public X509Certificate2 Certificat { get; }

    public X509Certificate2 Racine { get; }

    /// <summary>Identifiant de la clé de signature (ARN de la clé KMS).</summary>
    public string Cle { get; }

    internal ECDsa ClePublique { get; }

    /// <summary>
    /// Charge et vérifie l'autorité : l'intermédiaire est une CA (<c>pathlen:0</c>) signée par la
    /// racine, elle-même autosignée. La correspondance avec la clé KMS est vérifiée à chaque
    /// émission par <c>testSecret</c> (chaîne complète).
    /// </summary>
    public static Result<AutoriteIntermediaire> Charger(string intermediairePem, string racinePem, string cle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cle);

        X509Certificate2 intermediaire;
        X509Certificate2 racine;
        try
        {
            intermediaire = X509Certificate2.CreateFromPem(intermediairePem);
            racine = X509Certificate2.CreateFromPem(racinePem);
        }
        catch (CryptographicException ex)
        {
            return Echec("pki.autorite.pem", $"Certificat d'autorité illisible : {ex.Message}");
        }

        if (!EstAutorite(intermediaire, longueurChemin: 0))
        {
            return Echec("pki.autorite.intermediaire", "Le certificat intermédiaire n'est pas une CA de longueur de chemin 0.");
        }

        if (!EstAutorite(racine, longueurChemin: null) || racine.SubjectName.RawData.AsSpan().SequenceEqual(racine.IssuerName.RawData) is false)
        {
            return Echec("pki.autorite.racine", "Le certificat racine n'est pas une CA autosignée.");
        }

        if (!intermediaire.IssuerName.RawData.AsSpan().SequenceEqual(racine.SubjectName.RawData) || !EstSignePar(intermediaire, racine))
        {
            return Echec("pki.autorite.chaine", "Le certificat intermédiaire n'est pas signé par la racine.");
        }

        if (intermediaire.GetECDsaPublicKey() is not { } clePublique || clePublique.KeySize != 384)
        {
            return Echec("pki.autorite.cle", "La clé de l'intermédiaire doit être ECDSA P-384.");
        }

        return Result.Success(new AutoriteIntermediaire(intermediaire, racine, cle, clePublique));
    }

    public void Dispose()
    {
        ClePublique.Dispose();
        Certificat.Dispose();
        Racine.Dispose();
    }

    private static bool EstAutorite(X509Certificate2 certificat, int? longueurChemin) =>
        certificat.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault() is { CertificateAuthority: true } contraintes
        && (longueurChemin is null || (contraintes.HasPathLengthConstraint && contraintes.PathLengthConstraint == longueurChemin));

    private static bool EstSignePar(X509Certificate2 certificat, X509Certificate2 emetteur)
    {
        using var chaine = new X509Chain();
        chaine.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chaine.ChainPolicy.CustomTrustStore.Add(emetteur);
        chaine.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chaine.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid;
        return chaine.Build(certificat);
    }

    private static Result<AutoriteIntermediaire> Echec(string code, string message) =>
        Result.Failure<AutoriteIntermediaire>(new DomainError(code, message));
}
