using Ledger.Query.Application.Consultations;
using Ledger.Query.Tests.Support;
using static Ledger.Query.Tests.Support.JeuEssai;

namespace Ledger.Query.Application.Tests.Consultations;

/// <summary>Solde d'un compte et balance de vérification : courants ou à une date d'affaires.</summary>
public sealed class SoldeEtBalanceTests : IDisposable
{
    private static readonly DateOnly _le10Mars = new(2026, 3, 10);
    private static readonly DateOnly _le20Mars = new(2026, 3, 20);

    private readonly LectureEssai _lecture = new();

    public SoldeEtBalanceTests() =>
        _lecture.Amorcer(new JeuEssai(LectureEssai.Entite)
            .Compte("1000", "Encaisse")
            .Compte("2310", "TPS à percevoir", "Credit")
            .Compte("4000", "Ventes", "Credit")
            .Compte("5000", "Achats")
            .Ecriture(Guid.CreateVersion7(), _le10Mars, 1, [Debit("1000", 105m), Credit("4000", 100m), Credit("2310", 5m)])
            .Ecriture(Guid.CreateVersion7(), _le20Mars, 2, [Debit("5000", 30m), Credit("1000", 30m)]));

    [Fact]
    public async Task Le_solde_courant_est_positif_dans_le_sens_normal_du_compte()
    {
        var encaisse = await SoldeAsync("1000");
        var ventes = await SoldeAsync("4000");

        Assert.Equal(new SoldeDevise("CAD", 105m, 30m, 75m), Assert.Single(encaisse.Soldes));
        Assert.Equal(new SoldeDevise("CAD", 0m, 100m, 100m), Assert.Single(ventes.Soldes));
        Assert.Equal(("Encaisse", "Debit", (DateOnly?)null), (encaisse.Compte.Nom, encaisse.Compte.SensNormal, encaisse.AuPlusTard));
    }

    [Fact]
    public async Task Le_solde_a_une_date_ne_compte_que_les_lignes_jusqu_a_cette_date_incluse()
    {
        var veille = await SoldeAsync("1000", new DateOnly(2026, 3, 19));
        var jour = await SoldeAsync("1000", _le20Mars);

        Assert.Equal(105m, Assert.Single(veille.Soldes).Solde);
        Assert.Equal(75m, Assert.Single(jour.Soldes).Solde);
        Assert.Equal(new DateOnly(2026, 3, 19), veille.AuPlusTard);
    }

    [Fact]
    public async Task Un_compte_sans_mouvement_a_un_solde_nul_dans_sa_devise()
    {
        _lecture.Amorcer(new JeuEssai(LectureEssai.Entite).Compte("1100", "Clients USD", devise: "USD"));

        var clients = await SoldeAsync("1100");
        var avantTout = await SoldeAsync("1000", new DateOnly(2026, 1, 1));

        Assert.Equal(new SoldeDevise("USD", 0m, 0m, 0m), Assert.Single(clients.Soldes));
        Assert.Equal(new SoldeDevise("CAD", 0m, 0m, 0m), Assert.Single(avantTout.Soldes));
    }

    [Fact]
    public async Task Un_compte_absent_du_plan_comptable_est_introuvable()
    {
        var resultat = await _lecture.Consultation<ConsulterSoldeCompte>().ExecuterAsync(LectureEssai.Entite, "9999", null, LectureEssai.Ct);

        Assert.Equal(ErreursConsultation.CompteIntrouvableCode, resultat.Error.Code);
        Assert.Equal("9999", resultat.Error.Details!["accountId"]);
    }

    [Fact]
    public async Task La_balance_courante_est_equilibree_et_triee_par_compte()
    {
        var balance = await _lecture.Consultation<ConsulterBalanceVerification>().ExecuterAsync(LectureEssai.Entite, null, LectureEssai.Ct);

        Assert.Equal(
            [
                new LigneBalance("1000", "Encaisse", "CAD", 105m, 30m, 75m, 0m),
                new LigneBalance("2310", "TPS à percevoir", "CAD", 0m, 5m, 0m, 5m),
                new LigneBalance("4000", "Ventes", "CAD", 0m, 100m, 0m, 100m),
                new LigneBalance("5000", "Achats", "CAD", 30m, 0m, 30m, 0m),
            ],
            balance.Lignes);
        Assert.Equal(new TotalBalance("CAD", 135m, 135m, 105m, 105m), Assert.Single(balance.Totaux));
        Assert.True(balance.Equilibree);
    }

    [Fact]
    public async Task La_balance_a_une_date_ignore_les_ecritures_posterieures()
    {
        var balance = await _lecture.Consultation<ConsulterBalanceVerification>().ExecuterAsync(LectureEssai.Entite, _le10Mars, LectureEssai.Ct);

        Assert.Equal(["1000", "2310", "4000"], balance.Lignes.Select(l => l.Compte));
        Assert.True(balance.Equilibree);
        Assert.Equal(_le10Mars, balance.AuPlusTard);
    }

    [Fact]
    public async Task La_balance_totalise_chaque_devise_et_nomme_seulement_les_comptes_connus()
    {
        _lecture.Amorcer(new JeuEssai(LectureEssai.Entite).Ecriture(Guid.CreateVersion7(), _le20Mars, 3, [Debit("1001", 20m, "USD"), Credit("4001", 20m, "USD")]));

        var balance = await _lecture.Consultation<ConsulterBalanceVerification>().ExecuterAsync(LectureEssai.Entite, null, LectureEssai.Ct);

        Assert.Equal(["CAD", "USD"], balance.Totaux.Select(t => t.Devise));
        Assert.Null(balance.Lignes.Single(l => l.Compte == "1001").Nom);
        Assert.True(balance.Equilibree);
    }

    [Fact]
    public void Une_devise_desequilibree_rend_la_balance_desequilibree()
    {
        var balance = new BalanceVerification(
            LectureEssai.Entite,
            null,
            [],
            [new TotalBalance("CAD", 10m, 10m, 0m, 0m), new TotalBalance("USD", 10m, 9m, 1m, 0m)],
            new Application.Fraicheur.EtatFraicheur(0, 0, false));

        Assert.False(balance.Equilibree);
    }

    [Fact]
    public async Task Une_entite_sans_ecriture_a_une_balance_vide_et_equilibree()
    {
        var balance = await _lecture.Consultation<ConsulterBalanceVerification>().ExecuterAsync("INCONNUE", null, LectureEssai.Ct);

        Assert.Empty(balance.Lignes);
        Assert.True(balance.Equilibree);
    }

    public void Dispose() => _lecture.Dispose();

    private async Task<SoldeCompte> SoldeAsync(string compte, DateOnly? auPlusTard = null) =>
        (await _lecture.Consultation<ConsulterSoldeCompte>().ExecuterAsync(LectureEssai.Entite, compte, auPlusTard, LectureEssai.Ct)).Value;
}
