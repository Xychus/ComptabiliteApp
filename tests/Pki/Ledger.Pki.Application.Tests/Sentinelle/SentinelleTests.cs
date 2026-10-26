using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Sentinelle;
using Ledger.Pki.Tests.Support;

namespace Ledger.Pki.Application.Tests.Sentinelle;

public sealed class SentinelleTests
{
    // Créneaux d'une heure sur 3 nœuds : 2026-10-25 12:00 UTC est dans le créneau (heures depuis l'époque) % 3.
    private static readonly int _creneauCourant = (int)(AutoriteEssai.Maintenant.ToUnixTimeSeconds() / 3600 % 3);

    private readonly SourceEssai _source = new();
    private readonly EcrivainEssai _ecrivain = new();
    private readonly SanteEssai _sante = new();
    private readonly HorlogeEssai _horloge = new(AutoriteEssai.Maintenant);

    private Application.Sentinelle.Sentinelle Sentinelle(int? creneau = null) =>
        new(_source, _ecrivain, _sante, _horloge, new OptionsSentinelle { Creneau = creneau ?? _creneauCourant });

    private static ContenuSecretCertificat Contenu(DateTimeOffset expireLe) => new("c", "c", "k", "r", expireLe);

    private async Task InstallerAsync(string version, DateTimeOffset expireLe)
    {
        _source.Courant = new CertificatCourant(version, Contenu(expireLe));
        await Sentinelle().InstallerAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task InstallerAsync_ecrit_le_certificat_courant()
    {
        await InstallerAsync("v1", AutoriteEssai.Maintenant.AddDays(7));

        Assert.Equal(new CertificatInstalle("v1", AutoriteEssai.Maintenant.AddDays(7)), _ecrivain.Installe);
    }

    [Fact]
    public async Task VerifierAsync_certificat_a_jour_continue()
    {
        await InstallerAsync("v1", AutoriteEssai.Maintenant.AddDays(7));

        var verdict = await Sentinelle().VerifierAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DecisionSentinelle.Continuer, verdict.Decision);
        Assert.Equal(0, _sante.Appels);
    }

    [Fact]
    public async Task VerifierAsync_nouveau_certificat_dans_le_creneau_et_grappe_saine_redemarre()
    {
        await InstallerAsync("v1", AutoriteEssai.Maintenant.AddDays(2));
        _source.Courant = new CertificatCourant("v2", Contenu(AutoriteEssai.Maintenant.AddDays(7)));

        var verdict = await Sentinelle().VerifierAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DecisionSentinelle.Redemarrer, verdict.Decision);
        Assert.Same(_source.Courant.Contenu, _sante.IdentiteUtilisee);
    }

    [Fact]
    public async Task VerifierAsync_hors_creneau_attend()
    {
        await InstallerAsync("v1", AutoriteEssai.Maintenant.AddDays(2));
        _source.Courant = new CertificatCourant("v2", Contenu(AutoriteEssai.Maintenant.AddDays(7)));

        var verdict = await Sentinelle(creneau: (_creneauCourant + 1) % 3).VerifierAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DecisionSentinelle.Continuer, verdict.Decision);
        Assert.Contains("créneau", verdict.Raison, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifierAsync_grappe_non_saine_differe()
    {
        await InstallerAsync("v1", AutoriteEssai.Maintenant.AddDays(2));
        _source.Courant = new CertificatCourant("v2", Contenu(AutoriteEssai.Maintenant.AddDays(7)));
        _sante.Etat = new EtatGrappe(false, "1 partition sous-répliquée");

        var verdict = await Sentinelle().VerifierAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DecisionSentinelle.Continuer, verdict.Decision);
        Assert.Contains("non saine", verdict.Raison, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifierAsync_urgence_ignore_le_creneau_mais_pas_la_sante()
    {
        await InstallerAsync("v1", AutoriteEssai.Maintenant.AddHours(6));
        _source.Courant = new CertificatCourant("v2", Contenu(AutoriteEssai.Maintenant.AddDays(7)));
        var horsCreneau = Sentinelle(creneau: (_creneauCourant + 1) % 3);

        var saine = await horsCreneau.VerifierAsync(TestContext.Current.CancellationToken);
        _sante.Etat = new EtatGrappe(false, "2 nœuds sur 3");
        var malade = await horsCreneau.VerifierAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DecisionSentinelle.Redemarrer, saine.Decision);
        Assert.Contains("urgence", saine.Raison, StringComparison.Ordinal);
        Assert.Equal(DecisionSentinelle.Continuer, malade.Decision);
    }

    [Fact]
    public async Task VerifierAsync_rien_d_installe_traite_comme_nouveau_certificat()
    {
        _source.Courant = new CertificatCourant("v1", Contenu(AutoriteEssai.Maintenant.AddDays(7)));

        var verdict = await Sentinelle(creneau: (_creneauCourant + 1) % 3).VerifierAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DecisionSentinelle.Redemarrer, verdict.Decision);
    }

    private sealed class SourceEssai : ISourceCertificatCourant
    {
        public CertificatCourant Courant { get; set; } = new("v0", new ContenuSecretCertificat("c", "c", "k", "r", AutoriteEssai.Maintenant));

        public Task<CertificatCourant> LireAsync(CancellationToken cancellationToken) => Task.FromResult(Courant);

        public Task<string> LireVersionAsync(CancellationToken cancellationToken) => Task.FromResult(Courant.Version);
    }

    private sealed class EcrivainEssai : IEcrivainCertificats
    {
        public CertificatInstalle? Installe { get; private set; }

        public Task EcrireAsync(CertificatCourant certificat, CancellationToken cancellationToken)
        {
            Installe = new CertificatInstalle(certificat.Version, certificat.Contenu.ExpireLe);
            return Task.CompletedTask;
        }

        public Task<CertificatInstalle?> LireInstalleAsync(CancellationToken cancellationToken) => Task.FromResult(Installe);
    }

    private sealed class SanteEssai : ISanteGrappe
    {
        public EtatGrappe Etat { get; set; } = new(true, "3 nœuds");

        public int Appels { get; private set; }

        public ContenuSecretCertificat? IdentiteUtilisee { get; private set; }

        public Task<EtatGrappe> EvaluerAsync(ContenuSecretCertificat identite, CancellationToken cancellationToken)
        {
            Appels++;
            IdentiteUtilisee = identite;
            return Task.FromResult(Etat);
        }
    }
}
