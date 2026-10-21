using Ledger.Query.Application.Consultations;
using Ledger.Query.Application.Fraicheur;
using Ledger.Query.Application.Lecture;
using Ledger.Query.Tests.Support;

namespace Ledger.Query.Application.Tests.Fraicheur;

/// <summary>
/// Indicateur de fraîcheur : une réponse est périmée si un événement attend d'être projeté depuis
/// plus que le seuil, jamais parce que le journal est calme.
/// </summary>
public sealed class FraicheurTests : IDisposable
{
    private static readonly DateTimeOffset _maintenant = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly LectureEssai _lecture = new(TimeSpan.FromSeconds(30));

    public FraicheurTests() => _lecture.Horloge.UtcNow = _maintenant;

    [Fact]
    public async Task Des_projections_a_jour_ne_sont_jamais_perimees_meme_si_le_journal_est_calme_depuis_longtemps()
    {
        _lecture.Donnees.Modifier(t => t.AvecEvenement(1, _maintenant.AddDays(-90)).AvecEvenement(2, _maintenant.AddDays(-60)).AJourJusqua(2));

        var etat = await EvaluerAsync(NomsProjections.Soldes, NomsProjections.PlanComptable);

        Assert.Equal(new EtatFraicheur(2, 2, false), etat);
    }

    [Fact]
    public async Task Un_evenement_en_attente_depuis_plus_que_le_seuil_rend_la_reponse_perimee()
    {
        _lecture.Donnees.Modifier(t => t.AvecEvenement(1, _maintenant.AddMinutes(-5)).AvecEvenement(2, _maintenant.AddMinutes(-1)).AJourJusqua(1));

        var etat = await EvaluerAsync(NomsProjections.Soldes);

        Assert.Equal(new EtatFraicheur(1, 2, true), etat);
    }

    [Fact]
    public async Task Un_retard_recent_sous_le_seuil_n_est_pas_perime()
    {
        _lecture.Donnees.Modifier(t => t.AvecEvenement(1, _maintenant.AddMinutes(-5)).AvecEvenement(2, _maintenant.AddSeconds(-30)).AJourJusqua(1));

        var etat = await EvaluerAsync(NomsProjections.Soldes);

        Assert.Equal(new EtatFraicheur(1, 2, false), etat);
    }

    [Fact]
    public async Task La_projection_la_plus_en_retard_determine_la_fraicheur()
    {
        _lecture.Donnees.Modifier(t => t
            .AvecEvenement(1, _maintenant.AddMinutes(-10))
            .AvecEvenement(2, _maintenant.AddMinutes(-9))
            .AJourJusqua(2)
            .AvecPosition(NomsProjections.PlanComptable, 1));

        var etat = await EvaluerAsync(NomsProjections.Soldes, NomsProjections.PlanComptable);

        Assert.Equal(new EtatFraicheur(1, 2, true), etat);
    }

    [Fact]
    public async Task Un_journal_vide_n_est_pas_perime()
    {
        var etat = await EvaluerAsync(NomsProjections.Taxes);

        Assert.Equal(new EtatFraicheur(0, 0, false), etat);
    }

    [Fact]
    public async Task Chaque_consultation_rend_compte_des_projections_qu_elle_lit()
    {
        _lecture.Amorcer(new JeuEssai(LectureEssai.Entite).Compte("1000", "Encaisse").Periode(JeuEssai.Mars));
        _lecture.Donnees.Modifier(t => t.AvecEvenement(1, _maintenant.AddHours(-1)).AJourJusqua(1).AvecPosition(NomsProjections.Taxes, 0));

        var solde = (await _lecture.Consultation<ConsulterSoldeCompte>().ExecuterAsync(LectureEssai.Entite, "1000", null, LectureEssai.Ct)).Value;
        var periode = (await _lecture.Consultation<ConsulterPeriode>().ExecuterAsync(LectureEssai.Entite, JeuEssai.Mars, LectureEssai.Ct)).Value;
        var taxes = await _lecture.Consultation<ConsulterDeclarationTpsTvq>().ExecuterAsync(LectureEssai.Entite, JeuEssai.Mars, LectureEssai.Ct);

        Assert.False(solde.Fraicheur.Perimee);
        Assert.False(periode.Fraicheur.Perimee);
        Assert.True(taxes.Fraicheur.Perimee);
    }

    public void Dispose() => _lecture.Dispose();

    private Task<EtatFraicheur> EvaluerAsync(params string[] projections) =>
        _lecture.Consultation<EvaluateurFraicheur>().EvaluerAsync(projections, LectureEssai.Ct);
}
