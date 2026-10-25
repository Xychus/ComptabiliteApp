namespace Ledger.Pki.Application.Emission;

/// <summary>
/// Port de signature par une clé d'autorité non extractible (clé KMS asymétrique ECC P-384 de la
/// CA intermédiaire, ADR 0010). La clé privée ne quitte jamais le signataire.
/// </summary>
public interface ISignataireAutorite
{
    /// <summary>Signe un condensé SHA-384 ; renvoie la signature ECDSA encodée en DER (RFC 3279), la forme d'X.509.</summary>
    Task<byte[]> SignerCondenseAsync(string cle, ReadOnlyMemory<byte> condense, CancellationToken cancellationToken);

    /// <summary>Clé publique au format <c>SubjectPublicKeyInfo</c> (DER).</summary>
    Task<byte[]> LireClePubliqueAsync(string cle, CancellationToken cancellationToken);
}
