using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ledger.SharedKernel;

namespace Ledger.Pki.Application.Emission;

/// <summary>
/// Cérémonie annuelle (runbook <c>pki-ceremonie-annuelle</c>) : produit la demande de signature
/// (CSR, PKCS#10) d'une nouvelle intermédiaire dont la clé privée est une clé KMS. Seule la
/// demande sort d'AWS ; elle est signée hors ligne par la racine.
/// </summary>
public sealed class DemandeSignatureIntermediaire(ISignataireAutorite signataire)
{
    public async Task<Result<string>> ProduireAsync(string cle, string sujet, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cle);
        ArgumentException.ThrowIfNullOrWhiteSpace(sujet);

        X500DistinguishedName nom;
        try
        {
            nom = new X500DistinguishedName(sujet);
        }
        catch (CryptographicException ex)
        {
            return Result.Failure<string>(new DomainError("pki.csr.sujet", $"Sujet invalide : {ex.Message}"));
        }

        var infoClePublique = await signataire.LireClePubliqueAsync(cle, cancellationToken).ConfigureAwait(false);
        using var clePublique = ECDsa.Create();
        clePublique.ImportSubjectPublicKeyInfo(infoClePublique, out _);
        if (clePublique.KeySize != 384)
        {
            return Result.Failure<string>(new DomainError("pki.csr.cle", "La clé de l'intermédiaire doit être ECDSA P-384 (ECC_NIST_P384)."));
        }

        var demande = new CertificateRequest(nom, clePublique, HashAlgorithmName.SHA384);
        demande.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        demande.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));

        var pem = await SignatureDifferee.ConstruireAsync(
            signataire,
            cle,
            clePublique,
            generateur => demande.CreateSigningRequestPem(generateur),
            cancellationToken).ConfigureAwait(false);
        return Result.Success(pem);
    }
}
