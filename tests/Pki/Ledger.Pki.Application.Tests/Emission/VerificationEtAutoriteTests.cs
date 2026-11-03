using System.Security.Cryptography;
using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Profils;
using Ledger.Pki.Tests.Support;

namespace Ledger.Pki.Application.Tests.Emission;

public sealed class VerificationEtAutoriteTests : IDisposable
{
    private static readonly ProfilCertificat _profil = new("query-api", ["query-api.ledger-dev.interne"], UsageCertificat.Serveur);

    private readonly AutoriteEssai _pki = new();

    public void Dispose() => _pki.Dispose();

    private Task<ContenuSecretCertificat> EmettreAsync() =>
        _pki.Emetteur().EmettreAsync(_profil, TestContext.Current.CancellationToken).ContinueWith(t => t.Result.Value, TaskScheduler.Default);

    [Fact]
    public async Task Verifier_certificat_emis_est_accepte()
    {
        var contenu = await EmettreAsync();

        Assert.True(_pki.Verificateur().Verifier(contenu, _profil).IsSuccess);
    }

    [Fact]
    public async Task Verifier_autre_nom_commun_est_refuse()
    {
        var contenu = await EmettreAsync();

        var resultat = _pki.Verificateur().Verifier(contenu, _profil with { NomCommun = "command-api" });

        Assert.Equal("pki.verification.identite", resultat.Error.Code);
    }

    [Fact]
    public async Task Verifier_autres_noms_dns_sont_refuses()
    {
        var contenu = await EmettreAsync();

        var resultat = _pki.Verificateur().Verifier(contenu, _profil with { NomsDns = ["autre.ledger-dev.interne"] });

        Assert.Equal("pki.verification.identite", resultat.Error.Code);
    }

    [Fact]
    public async Task Verifier_cle_d_un_autre_certificat_est_refusee()
    {
        var contenu = await EmettreAsync();
        var autre = await EmettreAsync();

        var resultat = _pki.Verificateur().Verifier(contenu with { Cle = autre.Cle }, _profil);

        Assert.Equal("pki.verification.appariement", resultat.Error.Code);
    }

    [Fact]
    public async Task Verifier_chaine_ne_commencant_pas_par_le_final_est_refusee()
    {
        var contenu = await EmettreAsync();
        var autre = await EmettreAsync();

        var resultat = _pki.Verificateur().Verifier(contenu with { Certificat = autre.Certificat }, _profil);

        Assert.Equal("pki.verification.chaine", resultat.Error.Code);
    }

    [Fact]
    public async Task Verifier_racine_inattendue_est_refusee()
    {
        var contenu = await EmettreAsync();
        using var autrePki = new AutoriteEssai(nomRacine: "CN=Autre racine");

        var resultat = _pki.Verificateur().Verifier(contenu with { Racine = autrePki.Racine.ExportCertificatePem() }, _profil);

        Assert.Equal("pki.verification.racine", resultat.Error.Code);
    }

    [Fact]
    public async Task Verifier_certificat_d_une_autre_pki_est_refuse()
    {
        using var autrePki = new AutoriteEssai(nomRacine: "CN=Autre racine");
        var etranger = (await autrePki.Emetteur().EmettreAsync(_profil, TestContext.Current.CancellationToken)).Value;

        var resultat = _pki.Verificateur().Verifier(etranger with { Racine = _pki.Racine.ExportCertificatePem() }, _profil);

        Assert.Equal("pki.verification.chaine", resultat.Error.Code);
    }

    [Fact]
    public async Task Verifier_certificat_echu_est_refuse()
    {
        var contenu = await EmettreAsync();
        _pki.Horloge.UtcNow = AutoriteEssai.Maintenant.AddDays(8);

        Assert.Equal("pki.verification.chaine", _pki.Verificateur().Verifier(contenu, _profil).Error.Code);
    }

    [Fact]
    public void Charger_pem_illisible_est_refuse() =>
        Assert.Equal("pki.autorite.pem", AutoriteIntermediaire.Charger("pas un pem", _pki.Racine.ExportCertificatePem(), AutoriteEssai.Cle).Error.Code);

    [Fact]
    public void Charger_intermediaire_sans_contrainte_de_chemin_est_refuse()
    {
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var intermediaire = _pki.CreerIntermediaire(cle, AutoriteEssai.Maintenant.AddYears(1), pathLength: null);

        var resultat = AutoriteIntermediaire.Charger(intermediaire.ExportCertificatePem(), _pki.Racine.ExportCertificatePem(), AutoriteEssai.Cle);

        Assert.Equal("pki.autorite.intermediaire", resultat.Error.Code);
    }

    [Fact]
    public void Charger_intermediaire_qui_n_est_pas_une_autorite_est_refuse()
    {
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var intermediaire = _pki.CreerIntermediaire(cle, AutoriteEssai.Maintenant.AddYears(1), pathLength: null, autorite: false);

        var resultat = AutoriteIntermediaire.Charger(intermediaire.ExportCertificatePem(), _pki.Racine.ExportCertificatePem(), AutoriteEssai.Cle);

        Assert.Equal("pki.autorite.intermediaire", resultat.Error.Code);
    }

    [Fact]
    public void Charger_intermediaire_p256_est_refuse()
    {
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var intermediaire = _pki.CreerIntermediaire(cle, AutoriteEssai.Maintenant.AddYears(1), pathLength: 0);

        var resultat = AutoriteIntermediaire.Charger(intermediaire.ExportCertificatePem(), _pki.Racine.ExportCertificatePem(), AutoriteEssai.Cle);

        Assert.Equal("pki.autorite.cle", resultat.Error.Code);
    }

    [Fact]
    public void Charger_intermediaire_d_une_autre_racine_est_refuse()
    {
        using var autrePki = new AutoriteEssai(nomRacine: "CN=Autre racine");

        var resultat = AutoriteIntermediaire.Charger(_pki.Intermediaire.ExportCertificatePem(), autrePki.Racine.ExportCertificatePem(), AutoriteEssai.Cle);

        Assert.Equal("pki.autorite.chaine", resultat.Error.Code);
    }

    [Fact]
    public void Charger_racine_qui_n_est_pas_autosignee_est_refusee()
    {
        var resultat = AutoriteIntermediaire.Charger(_pki.Intermediaire.ExportCertificatePem(), _pki.Intermediaire.ExportCertificatePem(), AutoriteEssai.Cle);

        Assert.Equal("pki.autorite.racine", resultat.Error.Code);
    }
}
