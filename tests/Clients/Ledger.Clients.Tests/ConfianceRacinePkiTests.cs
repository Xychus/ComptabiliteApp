using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Ledger.Clients.Tests;

public sealed class ConfianceRacinePkiTests
{
    internal static readonly DateTimeOffset Debut = DateTimeOffset.UtcNow.AddDays(-1);

    [Fact]
    public void Une_chaine_jusqu_a_la_racine_de_la_pki_est_acceptee()
    {
        var (racine, intermediaire, serveur) = Pki("keycloak.ledger-test.internal");

        Assert.True(ConfianceRacinePki.Valider(racine, serveur, [intermediaire], SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Une_chaine_d_une_autre_pki_est_refusee()
    {
        var (_, intermediaire, serveur) = Pki("keycloak.ledger-test.internal");
        var (autreRacine, _, _) = Pki("autre");

        Assert.False(ConfianceRacinePki.Valider(autreRacine, serveur, [intermediaire], SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Fact]
    public void Sans_l_intermediaire_la_chaine_ne_se_construit_pas()
    {
        var (racine, _, serveur) = Pki("keycloak.ledger-test.internal");

        Assert.False(ConfianceRacinePki.Valider(racine, serveur, [], SslPolicyErrors.RemoteCertificateChainErrors));
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable)]
    public void Un_nom_d_hote_errone_ou_un_certificat_absent_est_refuse(SslPolicyErrors erreurs)
    {
        var (racine, intermediaire, serveur) = Pki("keycloak.ledger-test.internal");

        Assert.False(ConfianceRacinePki.Valider(racine, serveur, [intermediaire], erreurs));
        Assert.False(ConfianceRacinePki.Valider(racine, null, [intermediaire], SslPolicyErrors.None));
    }

    [Fact]
    public void Le_gestionnaire_n_impose_la_racine_que_si_elle_est_fournie()
    {
        var (racine, _, _) = Pki("x");

        using var avecRacine = ConfianceRacinePki.CreerGestionnaire(racine.ExportCertificatePem());

        Assert.Null(ConfianceRacinePki.CreerGestionnaire(null));
        Assert.Null(ConfianceRacinePki.CreerGestionnaire(" "));
        Assert.NotNull(avecRacine?.SslOptions.RemoteCertificateValidationCallback);
    }

    internal static (X509Certificate2 Racine, X509Certificate2 Intermediaire, X509Certificate2 Serveur) Pki(string nomDns)
    {
        using var cleRacine = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var cleIntermediaire = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var cleServeur = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var demandeRacine = new CertificateRequest("CN=Racine de test", cleRacine, HashAlgorithmName.SHA384);
        demandeRacine.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        demandeRacine.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        var racine = demandeRacine.CreateSelfSigned(Debut, Debut.AddYears(10));

        var demandeIntermediaire = new CertificateRequest("CN=Intermediaire de test", cleIntermediaire, HashAlgorithmName.SHA384);
        demandeIntermediaire.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        demandeIntermediaire.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var intermediaireSansCle = demandeIntermediaire.Create(racine, Debut, Debut.AddYears(1), [1]);
        var intermediaire = intermediaireSansCle.CopyWithPrivateKey(cleIntermediaire);

        var demandeServeur = new CertificateRequest($"CN={nomDns}", cleServeur, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(nomDns);
        demandeServeur.CertificateExtensions.Add(san.Build());
        demandeServeur.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var serveur = demandeServeur.Create(intermediaire, Debut, Debut.AddDays(7), [2]);

        return (racine, intermediaire, serveur);
    }
}
