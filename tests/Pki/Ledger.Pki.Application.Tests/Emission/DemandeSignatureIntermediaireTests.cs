using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ledger.Pki.Application.Emission;
using Ledger.Pki.Infrastructure.Memoire;

namespace Ledger.Pki.Application.Tests.Emission;

public sealed class DemandeSignatureIntermediaireTests
{
    private const string NouvelleCle = "arn:aws:kms:ca-central-1:111111111111:key/intermediaire-2027";

    [Fact]
    public async Task ProduireAsync_csr_signee_par_la_cle_et_marquee_autorite()
    {
        using var signataire = new SignataireMemoire();
        var cle = signataire.CreerCle(NouvelleCle);

        var pem = (await new DemandeSignatureIntermediaire(signataire)
            .ProduireAsync(NouvelleCle, "CN=Ledger dev AC intermediaire 2027, O=Ledger", TestContext.Current.CancellationToken)).Value;

        // LoadSigningRequestPem vérifie la signature de la demande par défaut.
        var demande = CertificateRequest.LoadSigningRequestPem(pem, HashAlgorithmName.SHA384, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
        Assert.Equal("CN=Ledger dev AC intermediaire 2027, O=Ledger", demande.SubjectName.Name);
        Assert.Equal(cle.ExportSubjectPublicKeyInfo(), demande.PublicKey.ExportSubjectPublicKeyInfo());
        var contraintes = demande.CertificateExtensions.OfType<X509BasicConstraintsExtension>().Single();
        Assert.True(contraintes.CertificateAuthority);
        Assert.Equal(0, contraintes.PathLengthConstraint);
    }

    [Fact]
    public async Task ProduireAsync_sujet_invalide_est_refuse()
    {
        using var signataire = new SignataireMemoire();
        signataire.CreerCle(NouvelleCle);

        var resultat = await new DemandeSignatureIntermediaire(signataire).ProduireAsync(NouvelleCle, "pas=un=nom,,", TestContext.Current.CancellationToken);

        Assert.Equal("pki.csr.sujet", resultat.Error.Code);
    }

    [Fact]
    public async Task ProduireAsync_cle_p256_est_refusee()
    {
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var resultat = await new DemandeSignatureIntermediaire(new SignatairePublic(cle.ExportSubjectPublicKeyInfo()))
            .ProduireAsync(NouvelleCle, "CN=Essai", TestContext.Current.CancellationToken);

        Assert.Equal("pki.csr.cle", resultat.Error.Code);
    }

    private sealed class SignatairePublic(byte[] clePublique) : ISignataireAutorite
    {
        public Task<byte[]> SignerCondenseAsync(string cle, ReadOnlyMemory<byte> condense, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<byte[]> LireClePubliqueAsync(string cle, CancellationToken cancellationToken) => Task.FromResult(clePublique);
    }
}
