using Ledger.Query.Application.Consultations;
using Ledger.Query.Application.Lecture;
using Ledger.Query.Tests.Support;
using static Ledger.Query.Tests.Support.JeuEssai;

namespace Ledger.Query.Application.Tests.Consultations;

/// <summary>Écriture, période et déclaration TPS/TVQ.</summary>
public sealed class EcriturePeriodeTaxesTests : IDisposable
{
    private readonly LectureEssai _lecture = new();

    [Fact]
    public async Task Une_ecriture_est_consultee_avec_ses_lignes()
    {
        var ecriture = Guid.CreateVersion7();
        _lecture.Amorcer(new JeuEssai(LectureEssai.Entite).Ecriture(ecriture, new DateOnly(2026, 3, 10), 4, [Debit("1000", 10m), Credit("4000", 10m)], "Vente"));

        var consultee = (await _lecture.Consultation<ConsulterEcriture>().ExecuterAsync(ecriture, LectureEssai.Ct)).Value;

        Assert.Equal(("Vente", 4L, 2), (consultee.Ecriture.Libelle, consultee.Ecriture.Sequence, consultee.Ecriture.Lignes.Count));
    }

    [Fact]
    public async Task Une_ecriture_inconnue_est_introuvable()
    {
        var inconnue = Guid.CreateVersion7();

        var resultat = await _lecture.Consultation<ConsulterEcriture>().ExecuterAsync(inconnue, LectureEssai.Ct);

        Assert.Equal(ErreursConsultation.EcritureIntrouvableCode, resultat.Error.Code);
        Assert.Equal(inconnue.ToString(), resultat.Error.Details!["entryId"]);
    }

    [Fact]
    public async Task Une_periode_scellee_est_consultee_avec_son_instantane()
    {
        _lecture.Amorcer(new JeuEssai(LectureEssai.Entite)
            .Periode(Mars, "Sealed", 2, Origine.AddDays(31), 3, new string('b', 64))
            .Instantane(Mars, 17, 3, new CumulCompte("1000", "CAD", 10m, 0m), new CumulCompte("4000", "CAD", 0m, 10m)));

        var consultee = (await _lecture.Consultation<ConsulterPeriode>().ExecuterAsync(LectureEssai.Entite, Mars, LectureEssai.Ct)).Value;

        Assert.Equal("Sealed", consultee.Periode.Statut);
        Assert.Equal(17, consultee.Instantane?.SequenceSceau);
    }

    [Fact]
    public async Task Une_periode_ouverte_n_a_pas_d_instantane()
    {
        _lecture.Amorcer(new JeuEssai(LectureEssai.Entite).Periode(Avril));

        var consultee = (await _lecture.Consultation<ConsulterPeriode>().ExecuterAsync(LectureEssai.Entite, Avril, LectureEssai.Ct)).Value;

        Assert.Null(consultee.Instantane);
    }

    [Fact]
    public async Task Une_periode_jamais_ouverte_est_introuvable()
    {
        var resultat = await _lecture.Consultation<ConsulterPeriode>().ExecuterAsync(LectureEssai.Entite, "2026-05", LectureEssai.Ct);

        Assert.Equal(ErreursConsultation.PeriodeIntrouvableCode, resultat.Error.Code);
        Assert.Equal("2026-05", resultat.Error.Details!["period"]);
    }

    /// <summary>Exemple de référence de l'ADR 0018 : vente 100 $ et achat 50 $ en mars.</summary>
    [Fact]
    public async Task La_declaration_donne_les_taxes_percues_payees_et_nettes()
    {
        _lecture.Amorcer(new JeuEssai(LectureEssai.Entite).Periode(Mars, "Sealed", 2).TaxesPeriode(Mars, "CAD", 5m, 2.5m, 9.98m, 4.99m));

        var declaration = await _lecture.Consultation<ConsulterDeclarationTpsTvq>().ExecuterAsync(LectureEssai.Entite, Mars, LectureEssai.Ct);

        var montants = Assert.Single(declaration.Montants);
        Assert.Equal((5m, 2.5m, 2.5m), (montants.TpsPercue, montants.TpsPayee, montants.TpsNette));
        Assert.Equal((9.98m, 4.99m, 4.99m), (montants.TvqPercue, montants.TvqPayee, montants.TvqNette));
        Assert.Equal("Sealed", declaration.StatutPeriode);
    }

    [Fact]
    public async Task Une_periode_sans_taxe_ni_ouverture_donne_une_declaration_vide_sans_statut()
    {
        var declaration = await _lecture.Consultation<ConsulterDeclarationTpsTvq>().ExecuterAsync(LectureEssai.Entite, "2026-05", LectureEssai.Ct);

        Assert.Empty(declaration.Montants);
        Assert.Null(declaration.StatutPeriode);
    }

    [Fact]
    public void Une_taxe_nette_negative_est_un_credit_a_recevoir()
    {
        var montants = new MontantsTpsTvq("CAD", 1m, 3m, 0m, 0.5m);

        Assert.Equal((-2m, -0.5m), (montants.TpsNette, montants.TvqNette));
    }

    public void Dispose() => _lecture.Dispose();
}
