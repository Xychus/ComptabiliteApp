using Ledger.Pki.Application.Rotation;

namespace Ledger.Pki.Infrastructure.Tests.Contrats;

/// <summary>
/// Suite de contrat de <see cref="IMagasinCertificats"/> (Liskov) : exécutée contre chaque
/// implémentation. Il n'existe pas d'image officielle émulant Secrets Manager : l'adaptateur AWS
/// est exercé à travers un client SDK simulé (<see cref="SecretsManagerEssai"/>).
/// </summary>
public abstract class ContratMagasinCertificats
{
    protected const string Secret = "ledger-dev/pki/essai";
    protected const string V1 = "00000000-0000-0000-0000-0000000000a1";
    protected const string V2 = "00000000-0000-0000-0000-0000000000a2";

    protected static readonly Dictionary<string, string> EtiquettesEssai = new(StringComparer.Ordinal) { ["pki:cn"] = "essai" };

    /// <summary>Magasin contenant <see cref="Secret"/>, vide, rotation activée, étiqueté <see cref="EtiquettesEssai"/>.</summary>
    protected abstract IMagasinCertificats Magasin { get; }

    /// <summary>Simule le lancement d'une rotation par Secrets Manager (version vide en attente).</summary>
    protected abstract void AmorcerRotation(string version);

    private static CancellationToken Jeton => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Decrire_secret_neuf_sans_version_avec_etiquettes()
    {
        var description = await Magasin.DecrireAsync(Secret, Jeton);

        Assert.True(description.RotationActivee);
        Assert.Empty(description.EtiquettesParVersion);
        Assert.Equal("essai", description.Etiquettes["pki:cn"]);
    }

    [Fact]
    public async Task Lire_version_amorcee_mais_vide_renvoie_null()
    {
        AmorcerRotation(V1);

        Assert.Null(await Magasin.LireVersionAsync(Secret, V1, Jeton));
        Assert.True((await Magasin.DecrireAsync(Secret, Jeton)).VersionPorte(V1, EtiquettesVersion.EnAttente));
    }

    [Fact]
    public async Task Ecrire_en_attente_puis_lire_par_version_et_par_etiquette()
    {
        AmorcerRotation(V1);

        await Magasin.EcrireEnAttenteAsync(Secret, V1, "valeur-1", Jeton);

        Assert.Equal("valeur-1", await Magasin.LireVersionAsync(Secret, V1, Jeton));
        Assert.Equal("valeur-1", await Magasin.LireParEtiquetteAsync(Secret, EtiquettesVersion.EnAttente, Jeton));
        Assert.Null(await Magasin.LireParEtiquetteAsync(Secret, EtiquettesVersion.Courante, Jeton));
    }

    [Fact]
    public async Task Ecrire_en_attente_meme_valeur_est_idempotent()
    {
        AmorcerRotation(V1);
        await Magasin.EcrireEnAttenteAsync(Secret, V1, "valeur-1", Jeton);

        await Magasin.EcrireEnAttenteAsync(Secret, V1, "valeur-1", Jeton);

        Assert.Equal("valeur-1", await Magasin.LireVersionAsync(Secret, V1, Jeton));
    }

    [Fact]
    public async Task Promouvoir_deplace_l_etiquette_courante()
    {
        AmorcerRotation(V1);
        await Magasin.EcrireEnAttenteAsync(Secret, V1, "valeur-1", Jeton);
        await Magasin.PromouvoirAsync(Secret, V1, null, Jeton);
        AmorcerRotation(V2);
        await Magasin.EcrireEnAttenteAsync(Secret, V2, "valeur-2", Jeton);

        await Magasin.PromouvoirAsync(Secret, V2, V1, Jeton);

        var description = await Magasin.DecrireAsync(Secret, Jeton);
        Assert.Equal(V2, description.VersionPortant(EtiquettesVersion.Courante));
        Assert.False(description.VersionPorte(V1, EtiquettesVersion.Courante));
        Assert.False(description.VersionPorte(V2, EtiquettesVersion.EnAttente));
        Assert.Equal("valeur-2", await Magasin.LireParEtiquetteAsync(Secret, EtiquettesVersion.Courante, Jeton));
    }
}
