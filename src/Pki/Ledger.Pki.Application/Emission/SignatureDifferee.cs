using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ledger.Pki.Application.Emission;

/// <summary>
/// Signe une structure X.509 (certificat ou demande de signature) avec un
/// <see cref="ISignataireAutorite"/> asynchrone. <see cref="X509SignatureGenerator"/> est
/// synchrone : une première construction capture les octets à signer, la signature est obtenue
/// de façon asynchrone, puis une seconde construction, déterministe, l'insère (les octets signés
/// sont comparés à ceux capturés).
/// </summary>
internal static class SignatureDifferee
{
    public static async Task<T> ConstruireAsync<T>(
        ISignataireAutorite signataire,
        string cle,
        ECDsa clePubliqueAutorite,
        Func<X509SignatureGenerator, T> construire,
        CancellationToken cancellationToken)
    {
        var capture = new Generateur(clePubliqueAutorite, signature: null);
        construire(capture);
        var aSigner = capture.DonneesSignees
            ?? throw new InvalidOperationException("Aucune donnée à signer n'a été produite.");

        var condense = SHA384.HashData(aSigner);
        var signature = await signataire.SignerCondenseAsync(cle, condense, cancellationToken).ConfigureAwait(false);

        var insertion = new Generateur(clePubliqueAutorite, signature);
        var resultat = construire(insertion);
        if (insertion.DonneesSignees is not { } signees || !signees.AsSpan().SequenceEqual(aSigner))
        {
            throw new InvalidOperationException("La structure X.509 n'est pas déterministe : signature refusée.");
        }

        return resultat;
    }

    private sealed class Generateur(ECDsa clePublique, byte[]? signature) : X509SignatureGenerator
    {
        private readonly X509SignatureGenerator _modele = CreateForECDsa(clePublique);

        public byte[]? DonneesSignees { get; private set; }

        public override byte[] GetSignatureAlgorithmIdentifier(HashAlgorithmName hashAlgorithm) =>
            hashAlgorithm == HashAlgorithmName.SHA384
                ? _modele.GetSignatureAlgorithmIdentifier(hashAlgorithm)
                : throw new ArgumentOutOfRangeException(nameof(hashAlgorithm), "Seul SHA-384 est accepté (clé P-384).");

        public override byte[] SignData(byte[] data, HashAlgorithmName hashAlgorithm)
        {
            DonneesSignees = data.ToArray();

            // Première passe : signature fictive de forme DER valide, jetée avec la structure.
            return signature ?? [0x30, 0x06, 0x02, 0x01, 0x01, 0x02, 0x01, 0x01];
        }

        protected override PublicKey BuildPublicKey() => PublicKey.CreateFromSubjectPublicKeyInfo(clePublique.ExportSubjectPublicKeyInfo(), out _);
    }
}
