using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ledger.Clients.Tests;

public sealed class IdentiteClientPkiTests
{
    [Fact]
    public void Le_secret_de_la_pki_donne_le_certificat_avec_sa_cle_et_l_intermediaire()
    {
        var (_, intermediaire, _) = ConfianceRacinePkiTests.Pki("x");
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var client = new CertificateRequest("CN=ledger-exemple", cle, HashAlgorithmName.SHA256)
            .Create(intermediaire, ConfianceRacinePkiTests.Debut, ConfianceRacinePkiTests.Debut.AddDays(7), [3]);

        using var identite = IdentiteClientPki.DepuisPem(
            client.ExportCertificatePem() + "\n" + intermediaire.ExportCertificatePem(),
            cle.ExportPkcs8PrivateKeyPem());

        Assert.True(identite.Certificat.HasPrivateKey);
        Assert.Equal("CN=ledger-exemple", identite.Certificat.Subject);
        Assert.Equal(intermediaire.Thumbprint, Assert.Single(identite.Intermediaires).Thumbprint);
        Assert.NotNull(identite.Contexte());
    }

    [Fact]
    public void Une_chaine_sans_certificat_est_refusee()
    {
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.Throws<ArgumentException>(() => IdentiteClientPki.DepuisPem("rien", cle.ExportPkcs8PrivateKeyPem()));
    }

    [Fact]
    public void Un_certificat_sans_cle_privee_est_refuse()
    {
        var (racine, _, _) = ConfianceRacinePkiTests.Pki("x");
        using var sansCle = X509CertificateLoader.LoadCertificate(racine.RawData);

        Assert.Throws<ArgumentException>(() => new IdentiteClientPki(sansCle, []));
    }
}
