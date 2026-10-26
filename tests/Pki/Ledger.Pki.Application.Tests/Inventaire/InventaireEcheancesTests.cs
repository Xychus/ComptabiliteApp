using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Inventaire;
using Ledger.Pki.Application.Profils;
using Ledger.Pki.Application.Rotation;
using Ledger.Pki.Infrastructure.Memoire;
using Ledger.Pki.Tests.Support;

namespace Ledger.Pki.Application.Tests.Inventaire;

public sealed class InventaireEcheancesTests : IDisposable
{
    private readonly AutoriteEssai _pki = new(validiteIntermediaire: TimeSpan.FromDays(200));
    private readonly MagasinCertificatsMemoire _magasin = new();
    private readonly PublicateurMesuresMemoire _publicateur = new();

    public void Dispose() => _pki.Dispose();

    private async Task AjouterAsync(string identite, DateTimeOffset? expireLe)
    {
        _magasin.Creer(identite, new Dictionary<string, string>(StringComparer.Ordinal) { [ProfilCertificat.EtiquetteNomCommun] = identite });
        if (expireLe is { } echeance)
        {
            _magasin.AmorcerRotation(identite, "v1");
            await _magasin.EcrireEnAttenteAsync(identite, "v1", new ContenuSecretCertificat("c", "c", "k", "r", echeance).VersJson(), TestContext.Current.CancellationToken);
            await _magasin.PromouvoirAsync(identite, "v1", null, TestContext.Current.CancellationToken);
        }
    }

    private InventaireEcheances Inventaire() => new(_magasin, _publicateur, _pki.Autorite, _pki.Horloge);

    [Fact]
    public async Task ExecuterAsync_publie_chaque_identite_le_minimum_et_l_intermediaire()
    {
        await AjouterAsync("kafka-1", AutoriteEssai.Maintenant.AddDays(5));
        await AjouterAsync("debezium", AutoriteEssai.Maintenant.AddDays(2.5));

        await Inventaire().ExecuterAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                new MesureEcheance("debezium", 2.5),
                new MesureEcheance("kafka-1", 5),
                new MesureEcheance(InventaireEcheances.IdentiteMinimum, 2.5),
                new MesureEcheance(InventaireEcheances.IdentiteIntermediaire, 200),
            ],
            _publicateur.Publiees);
    }

    [Fact]
    public async Task ExecuterAsync_certificat_jamais_emis_ou_echu_compte_zero()
    {
        await AjouterAsync("jamais-emis", null);
        await AjouterAsync("echu", AutoriteEssai.Maintenant.AddDays(-1));

        var mesures = await Inventaire().ExecuterAsync(TestContext.Current.CancellationToken);

        Assert.All(mesures.Where(m => m.Identite != InventaireEcheances.IdentiteIntermediaire), m => Assert.Equal(0, m.JoursRestants));
    }

    [Fact]
    public async Task ExecuterAsync_sans_certificat_publie_seulement_l_intermediaire()
    {
        var mesures = await Inventaire().ExecuterAsync(TestContext.Current.CancellationToken);

        Assert.Equal([InventaireEcheances.IdentiteIntermediaire], mesures.Select(m => m.Identite));
    }

    [Fact]
    public async Task ExecuterAsync_secret_sans_etiquette_pki_n_est_pas_recense()
    {
        _magasin.Creer("autre", new Dictionary<string, string>(StringComparer.Ordinal));

        var mesures = await Inventaire().ExecuterAsync(TestContext.Current.CancellationToken);

        Assert.DoesNotContain(mesures, m => m.Identite == "autre");
        Assert.Null(await _magasin.LireParEtiquetteAsync("autre", EtiquettesVersion.Courante, TestContext.Current.CancellationToken));
    }
}
