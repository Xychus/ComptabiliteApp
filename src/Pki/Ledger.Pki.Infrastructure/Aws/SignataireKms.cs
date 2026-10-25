using Amazon.KeyManagementService;
using Amazon.KeyManagementService.Model;
using Ledger.Pki.Application.Emission;

namespace Ledger.Pki.Infrastructure.Aws;

/// <summary>
/// Signature par une clé KMS asymétrique <c>ECC_NIST_P384</c> / <c>SIGN_VERIFY</c> : la clé
/// privée de l'intermédiaire n'est jamais extractible et chaque signature est tracée dans
/// CloudTrail. KMS renvoie une signature ECDSA encodée en DER, la forme attendue par X.509.
/// </summary>
public sealed class SignataireKms(IAmazonKeyManagementService kms) : ISignataireAutorite
{
    public async Task<byte[]> SignerCondenseAsync(string cle, ReadOnlyMemory<byte> condense, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream(condense.ToArray());
        var reponse = await kms.SignAsync(
            new SignRequest
            {
                KeyId = cle,
                Message = message,
                MessageType = MessageType.DIGEST,
                SigningAlgorithm = SigningAlgorithmSpec.ECDSA_SHA_384,
            },
            cancellationToken).ConfigureAwait(false);
        return reponse.Signature.ToArray();
    }

    public async Task<byte[]> LireClePubliqueAsync(string cle, CancellationToken cancellationToken)
    {
        var reponse = await kms.GetPublicKeyAsync(new GetPublicKeyRequest { KeyId = cle }, cancellationToken).ConfigureAwait(false);
        return reponse.PublicKey.ToArray();
    }
}
