using System.Security.Cryptography;
using Ledger.Pki.Application.Emission;

namespace Ledger.Pki.Infrastructure.Memoire;

/// <summary>Signataire en mémoire (clés ECDSA P-384 locales) : même contrat que la clé KMS, pour les tests.</summary>
public sealed class SignataireMemoire : ISignataireAutorite, IDisposable
{
    private readonly Dictionary<string, ECDsa> _cles = new(StringComparer.Ordinal);

    /// <summary>Crée une clé P-384 désignée par <paramref name="cle"/> et la renvoie (pour signer la CA dans les tests).</summary>
    public ECDsa CreerCle(string cle)
    {
        var nouvelle = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        _cles.Add(cle, nouvelle);
        return nouvelle;
    }

    public Task<byte[]> SignerCondenseAsync(string cle, ReadOnlyMemory<byte> condense, CancellationToken cancellationToken) =>
        Task.FromResult(Cle(cle).SignHash(condense.Span, DSASignatureFormat.Rfc3279DerSequence));

    public Task<byte[]> LireClePubliqueAsync(string cle, CancellationToken cancellationToken) =>
        Task.FromResult(Cle(cle).ExportSubjectPublicKeyInfo());

    public void Dispose()
    {
        foreach (var cle in _cles.Values)
        {
            cle.Dispose();
        }
    }

    private ECDsa Cle(string cle) =>
        _cles.TryGetValue(cle, out var trouvee) ? trouvee : throw new KeyNotFoundException($"Clé inconnue : {cle}.");
}
