using FsCheck;
using FsCheck.Xunit;
using Ledger.Query.Application.Consultations;
using Ledger.Query.Application.Lecture;
using Ledger.Query.Tests.Support;
using static Ledger.Query.Tests.Support.JeuEssai;

namespace Ledger.Query.Application.Tests.Consultations;

/// <summary>Sous-livre d'un compte : pagination par curseur dans l'ordre comptable.</summary>
public sealed class LignesCompteTests : IDisposable
{
    private readonly LectureEssai _lecture = new();

    public LignesCompteTests() => _lecture.Amorcer(new JeuEssai(LectureEssai.Entite).Compte("1000", "Encaisse").Compte("4000", "Ventes", "Credit"));

    [Fact]
    public async Task Les_pages_se_suivent_sans_trou_ni_doublon_et_la_derniere_n_a_pas_de_curseur()
    {
        AmorcerEcritures(5);

        var premiere = await PageAsync(null, 2);
        var deuxieme = await PageAsync(premiere.CurseurSuivant, 2);
        var derniere = await PageAsync(deuxieme.CurseurSuivant, 2);

        Assert.Equal([1L, 2L, 3L, 4L, 5L], premiere.Lignes.Concat(deuxieme.Lignes).Concat(derniere.Lignes).Select(l => l.Sequence));
        Assert.NotNull(premiere.CurseurSuivant);
        Assert.Null(derniere.CurseurSuivant);
    }

    [Fact]
    public async Task Une_page_exactement_pleine_en_fin_de_sous_livre_n_annonce_pas_de_suite()
    {
        AmorcerEcritures(2);

        var page = await PageAsync(null, 2);

        Assert.Equal(2, page.Lignes.Count);
        Assert.Null(page.CurseurSuivant);
    }

    [Fact]
    public async Task Un_compte_sans_ligne_donne_une_page_vide()
    {
        var page = await PageAsync(null, 10);

        Assert.Empty(page.Lignes);
        Assert.Null(page.CurseurSuivant);
    }

    [Fact]
    public async Task Une_ligne_ajoutee_avant_le_curseur_ne_decale_pas_la_page_suivante()
    {
        AmorcerEcritures(4);
        var premiere = await PageAsync(null, 2);

        _lecture.Amorcer(new JeuEssai(LectureEssai.Entite).Ecriture(Guid.CreateVersion7(), new DateOnly(2026, 3, 1), 99, [Debit("1000", 1m), Credit("4000", 1m)]));
        var suite = await PageAsync(premiere.CurseurSuivant, 2);

        Assert.Equal([3L, 4L], suite.Lignes.Select(l => l.Sequence));
    }

    [Fact]
    public async Task Un_curseur_illisible_est_rejete()
    {
        var resultat = await _lecture.Consultation<ConsulterLignesCompte>().ExecuterAsync(LectureEssai.Entite, "1000", "pas-un-curseur", 10, LectureEssai.Ct);

        Assert.Equal(ErreursConsultation.CurseurInvalideCode, resultat.Error.Code);
    }

    [Fact]
    public async Task Un_compte_absent_du_plan_comptable_est_introuvable()
    {
        var resultat = await _lecture.Consultation<ConsulterLignesCompte>().ExecuterAsync(LectureEssai.Entite, "9999", null, 10, LectureEssai.Ct);

        Assert.Equal(ErreursConsultation.CompteIntrouvableCode, resultat.Error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(ConsulterLignesCompte.TailleMaximale + 1)]
    public async Task Une_taille_hors_limites_est_une_erreur_de_programmation(int taille) =>
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _lecture.Consultation<ConsulterLignesCompte>().ExecuterAsync(LectureEssai.Entite, "1000", null, taille, LectureEssai.Ct));

    [Property]
    public bool Le_curseur_restitue_exactement_la_position_encodee(DateTime date, NonNegativeInt sequence, PositiveInt ligne)
    {
        var position = new PositionLigne(DateOnly.FromDateTime(date), sequence.Get * 3L, ligne.Get);
        return CurseurLignes.TryDecoder(CurseurLignes.Encoder(position), out var decodee) && decodee == position;
    }

    [Theory]
    [InlineData("")]
    [InlineData("%%%")]
    [InlineData("MnwyMDI2LTAzLTEwfDF8MQ")] // version 2
    [InlineData("MXwyMDI2LTEzLTEwfDF8MQ")] // mois 13
    [InlineData("MXwyMDI2LTAzLTEwfC0xfDE")] // séquence négative
    [InlineData("MXwyMDI2LTAzLTEwfDE")] // trois parties
    public void Un_curseur_mal_forme_n_est_pas_decode(string curseur)
    {
        Assert.False(CurseurLignes.TryDecoder(curseur, out var position));
        Assert.Null(position);
    }

    /// <summary>Concaténer les pages redonne exactement le sous-livre trié, pour toute taille de page.</summary>
    [Property(MaxTest = 100)]
    public bool La_pagination_parcourt_tout_le_sous_livre_quelle_que_soit_la_taille_de_page(byte ecritures, PositiveInt tailleTiree)
    {
        var nombre = ecritures % 31;
        var taille = (tailleTiree.Get % 7) + 1;
        using var lecture = new LectureEssai();
        var jeu = new JeuEssai(LectureEssai.Entite).Compte("1000", "Encaisse");
        for (var i = 1; i <= nombre; i++)
        {
            // Dates en désordre par rapport aux séquences, plusieurs lignes du compte par écriture.
            jeu.Ecriture(Guid.CreateVersion7(), new DateOnly(2026, 3, 1 + (i * 7 % 28)), i, [Debit("1000", i), Debit("1000", 1m), Credit("4000", i + 1m)]);
        }

        lecture.Amorcer(jeu);
        var consultation = lecture.Consultation<ConsulterLignesCompte>();
        var lues = new List<LigneCompte>();
        string? curseur = null;
        do
        {
            var page = consultation.ExecuterAsync(LectureEssai.Entite, "1000", curseur, taille, CancellationToken.None).GetAwaiter().GetResult().Value;
            lues.AddRange(page.Lignes);
            curseur = page.CurseurSuivant;
        }
        while (curseur is not null);

        var attendues = jeu.Lignes.Where(l => l.Compte == "1000").OrderBy(l => l.DateAffaires).ThenBy(l => l.Sequence).ThenBy(l => l.NumeroLigne);
        return lues.Select(l => (l.Ecriture, l.NumeroLigne)).SequenceEqual(attendues.Select(l => (l.Ecriture, l.NumeroLigne)));
    }

    public void Dispose() => _lecture.Dispose();

    private void AmorcerEcritures(int nombre)
    {
        var jeu = new JeuEssai(LectureEssai.Entite);
        for (var i = 1; i <= nombre; i++)
        {
            jeu.Ecriture(Guid.CreateVersion7(), new DateOnly(2026, 3, 10), i, [Debit("1000", i), Credit("4000", i)]);
        }

        _lecture.Amorcer(jeu);
    }

    private async Task<PageLignesCompte> PageAsync(string? curseur, int taille) =>
        (await _lecture.Consultation<ConsulterLignesCompte>().ExecuterAsync(LectureEssai.Entite, "1000", curseur, taille, LectureEssai.Ct)).Value;
}
