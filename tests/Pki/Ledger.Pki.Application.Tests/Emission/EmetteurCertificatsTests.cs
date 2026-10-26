using System.Security.Cryptography.X509Certificates;
using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Profils;
using Ledger.Pki.Tests.Support;

namespace Ledger.Pki.Application.Tests.Emission;

public sealed class EmetteurCertificatsTests : IDisposable
{
    private static readonly ProfilCertificat _broker = new("kafka-1", ["kafka-1.ledger-dev.interne"], UsageCertificat.Serveur | UsageCertificat.Client);
    private static readonly ProfilCertificat _client = new("debezium", [], UsageCertificat.Client);

    private readonly AutoriteEssai _pki = new();

    public void Dispose() => _pki.Dispose();

    [Fact]
    public async Task EmettreAsync_certificat_chaine_jusqu_a_la_racine()
    {
        var contenu = (await _pki.Emetteur().EmettreAsync(_broker, TestContext.Current.CancellationToken)).Value;

        using var certificat = X509Certificate2.CreateFromPem(contenu.Chaine, contenu.Cle);
        using var chaine = new X509Chain();
        chaine.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chaine.ChainPolicy.CustomTrustStore.Add(_pki.Racine);
        chaine.ChainPolicy.ExtraStore.Add(_pki.Intermediaire);
        chaine.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chaine.ChainPolicy.VerificationTime = AutoriteEssai.Maintenant.UtcDateTime;

        Assert.True(chaine.Build(certificat), string.Join(", ", chaine.ChainStatus.Select(s => s.Status)));
        Assert.True(certificat.HasPrivateKey);
        Assert.Equal(256, certificat.GetECDsaPublicKey()!.KeySize);
    }

    [Fact]
    public async Task EmettreAsync_identite_et_usages_du_profil()
    {
        var contenu = (await _pki.Emetteur().EmettreAsync(_broker, TestContext.Current.CancellationToken)).Value;

        using var certificat = X509Certificate2.CreateFromPem(contenu.Certificat);
        Assert.Equal("CN=kafka-1", certificat.Subject);
        Assert.Equal(_pki.Intermediaire.Subject, certificat.Issuer);
        Assert.Equal(["kafka-1.ledger-dev.interne"], certificat.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single().EnumerateDnsNames());
        var usages = certificat.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Select(o => o.Value);
        Assert.Equal(["1.3.6.1.5.5.7.3.1", "1.3.6.1.5.5.7.3.2"], usages);
        Assert.False(certificat.Extensions.OfType<X509BasicConstraintsExtension>().Single().CertificateAuthority);
    }

    [Fact]
    public async Task EmettreAsync_client_sans_san_ni_usage_serveur()
    {
        var contenu = (await _pki.Emetteur().EmettreAsync(_client, TestContext.Current.CancellationToken)).Value;

        using var certificat = X509Certificate2.CreateFromPem(contenu.Certificat);
        Assert.Empty(certificat.Extensions.OfType<X509SubjectAlternativeNameExtension>());
        var usages = certificat.Extensions.OfType<X509EnhancedKeyUsageExtension>().Single().EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>().Select(o => o.Value);
        Assert.Equal(["1.3.6.1.5.5.7.3.2"], usages);
    }

    [Fact]
    public async Task EmettreAsync_validite_de_7_jours_antidatee_de_5_minutes()
    {
        var contenu = (await _pki.Emetteur().EmettreAsync(_broker, TestContext.Current.CancellationToken)).Value;

        using var certificat = X509Certificate2.CreateFromPem(contenu.Certificat);
        Assert.Equal(AutoriteEssai.Maintenant.AddDays(7), contenu.ExpireLe);
        Assert.Equal(AutoriteEssai.Maintenant.AddDays(7).UtcDateTime, certificat.NotAfter.ToUniversalTime());
        Assert.Equal(AutoriteEssai.Maintenant.AddMinutes(-5).UtcDateTime, certificat.NotBefore.ToUniversalTime());
    }

    [Fact]
    public async Task EmettreAsync_echeance_bornee_par_celle_de_l_intermediaire()
    {
        using var pki = new AutoriteEssai(validiteIntermediaire: TimeSpan.FromDays(3));

        var contenu = (await pki.Emetteur().EmettreAsync(_broker, TestContext.Current.CancellationToken)).Value;

        Assert.Equal(new DateTimeOffset(pki.Intermediaire.NotAfter.ToUniversalTime()), contenu.ExpireLe);
    }

    [Fact]
    public async Task EmettreAsync_intermediaire_presque_echue_est_refusee()
    {
        using var pki = new AutoriteEssai(validiteIntermediaire: TimeSpan.FromDays(1));

        var resultat = await pki.Emetteur().EmettreAsync(_broker, TestContext.Current.CancellationToken);

        Assert.Equal("pki.emission.intermediaire-echu", resultat.Error.Code);
    }

    [Fact]
    public async Task EmettreAsync_numeros_de_serie_distincts_et_positifs()
    {
        var premier = (await _pki.Emetteur().EmettreAsync(_client, TestContext.Current.CancellationToken)).Value;
        var second = (await _pki.Emetteur().EmettreAsync(_client, TestContext.Current.CancellationToken)).Value;

        using var a = X509Certificate2.CreateFromPem(premier.Certificat);
        using var b = X509Certificate2.CreateFromPem(second.Certificat);
        Assert.NotEqual(a.SerialNumber, b.SerialNumber);
        Assert.True(a.SerialNumberBytes.Span[0] is > 0 and < 0x80, "Le numéro de série doit être positif, sur 16 octets.");
        Assert.Equal(16, a.SerialNumberBytes.Length);
    }

    [Fact]
    public async Task EmettreAsync_chaine_contient_le_final_puis_l_intermediaire_et_la_racine_est_fournie()
    {
        var contenu = (await _pki.Emetteur().EmettreAsync(_client, TestContext.Current.CancellationToken)).Value;

        Assert.StartsWith(contenu.Certificat, contenu.Chaine, StringComparison.Ordinal);
        Assert.Contains(_pki.Intermediaire.ExportCertificatePem(), contenu.Chaine, StringComparison.Ordinal);
        Assert.Equal(_pki.Racine.ExportCertificatePem(), contenu.Racine);
        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", contenu.Cle, StringComparison.Ordinal);
    }

    [Fact]
    public void ContenuSecretCertificat_aller_retour_json_sans_exposer_la_cle()
    {
        var contenu = new ContenuSecretCertificat("c", "c\ni", "k", "r", AutoriteEssai.Maintenant);

        Assert.Equal(contenu, ContenuSecretCertificat.DepuisJson(contenu.VersJson()));
        Assert.DoesNotContain("CLE-SECRETE", contenu.ToString(), StringComparison.Ordinal);
        Assert.Null(ContenuSecretCertificat.DepuisJson("{"));
        Assert.Null(ContenuSecretCertificat.DepuisJson("{}"));
    }
}
