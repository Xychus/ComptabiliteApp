using Ledger.Query.Application.Lecture;
using Ledger.Query.Tests.Support;
using static Ledger.Query.Tests.Support.JeuEssai;

namespace Ledger.Query.Infrastructure.Tests.Contrat;

/// <summary>
/// Suite de contrat des ports de lecture (Liskov) : exécutée contre l'implémentation en mémoire
/// et contre PostgreSQL. Mêmes résultats, mêmes ordres, mêmes cas vides.
/// </summary>
public abstract class ContratLecteurs
{
    private static readonly DateOnly _le10Mars = new(2026, 3, 10);
    private static readonly DateOnly _le20Mars = new(2026, 3, 20);
    private static readonly DateOnly _le5Avril = new(2026, 4, 5);

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Un_compte_du_plan_comptable_est_trouve_dans_son_entite_seulement()
    {
        await using var magasin = CreerMagasin();
        await magasin.AmorcerAsync(new JeuEssai(magasin.Entite).Compte("4000", "Ventes", "Credit"));
        await magasin.AmorcerAsync(new JeuEssai(magasin.AutreEntite).Compte("1000", "Encaisse"));

        var ventes = await magasin.PlanComptable.TrouverCompteAsync(magasin.Entite, "4000", Ct);

        Assert.Equal(new CompteLu(magasin.Entite, "4000", "Ventes", Devise, "Credit", true), ventes);
        Assert.Null(await magasin.PlanComptable.TrouverCompteAsync(magasin.Entite, "1000", Ct));
        Assert.Null(await magasin.PlanComptable.TrouverCompteAsync(magasin.AutreEntite, "4000", Ct));
    }

    [Fact]
    public async Task Les_comptes_d_une_entite_sont_tries_par_code_en_ordre_ordinal()
    {
        await using var magasin = CreerMagasin();
        await magasin.AmorcerAsync(new JeuEssai(magasin.Entite).Compte("2", "Deux").Compte("10", "Dix").Compte("B", "Bé").Compte("a", "a"));

        var comptes = await magasin.PlanComptable.LireComptesAsync(magasin.Entite, Ct);

        Assert.Equal(["10", "2", "B", "a"], comptes.Select(c => c.Compte));
    }

    [Fact]
    public async Task Les_soldes_courants_sont_les_cumuls_par_compte_et_devise()
    {
        await using var magasin = CreerMagasin();
        await magasin.AmorcerAsync(Ventes(magasin.Entite));

        var tous = await magasin.Soldes.LireSoldesAsync(magasin.Entite, null, Ct);
        var encaisse = await magasin.Soldes.LireSoldesAsync(magasin.Entite, "1000", Ct);

        Assert.Equal(
            [
                new CumulCompte("1000", "CAD", 150m, 0m),
                new CumulCompte("1000", "USD", 20m, 0m),
                new CumulCompte("4000", "CAD", 0m, 150m),
                new CumulCompte("4000", "USD", 0m, 20m),
            ],
            tous);
        Assert.Equal(2, encaisse.Count);
        Assert.Empty(await magasin.Soldes.LireSoldesAsync(magasin.Entite, "9999", Ct));
        Assert.Empty(await magasin.Soldes.LireSoldesAsync(magasin.AutreEntite, null, Ct));
    }

    [Fact]
    public async Task Les_mouvements_cumules_s_arretent_a_la_date_d_affaires_incluse()
    {
        await using var magasin = CreerMagasin();
        await magasin.AmorcerAsync(Ventes(magasin.Entite));

        var avantTout = await magasin.Mouvements.CumulerAsync(magasin.Entite, null, new DateOnly(2026, 3, 9), Ct);
        var le10 = await magasin.Mouvements.CumulerAsync(magasin.Entite, "1000", _le10Mars, Ct);
        var tout = await magasin.Mouvements.CumulerAsync(magasin.Entite, null, _le5Avril, Ct);

        Assert.Empty(avantTout);
        Assert.Equal([new CumulCompte("1000", "CAD", 100m, 0m)], le10);
        Assert.Equal(await magasin.Soldes.LireSoldesAsync(magasin.Entite, null, Ct), tout);
    }

    [Fact]
    public async Task Le_sous_livre_suit_l_ordre_comptable_et_porte_le_libelle_de_l_ecriture()
    {
        await using var magasin = CreerMagasin();
        var tardive = Guid.CreateVersion7();
        var precoce = Guid.CreateVersion7();
        var double1000 = Guid.CreateVersion7();

        // Séquence plus petite mais date d'affaires plus tardive : l'ordre comptable prime.
        await magasin.AmorcerAsync(new JeuEssai(magasin.Entite)
            .Ecriture(tardive, _le20Mars, 5, [Debit("1000", 1m), Credit("4000", 1m)], "Tardive")
            .Ecriture(precoce, _le10Mars, 9, [Debit("1000", 2m), Credit("4000", 2m)], "Précoce")
            .Ecriture(double1000, _le10Mars, 12, [Debit("1000", 3m), Debit("1000", 4m), Credit("4000", 7m)]));

        var lignes = await magasin.LignesCompte.LireLignesAsync(magasin.Entite, "1000", null, 10, Ct);

        Assert.Equal(
            [(precoce, 1), (double1000, 1), (double1000, 2), (tardive, 1)],
            lignes.Select(l => (l.Ecriture, l.NumeroLigne)));
        Assert.Equal(
            new LigneCompte(precoce, 1, _le10Mars, Mars, "Debit", 2m, Devise, 9, "Précoce"),
            lignes[0]);
        Assert.Null(lignes[1].Libelle);
    }

    [Fact]
    public async Task Le_sous_livre_reprend_strictement_apres_la_position_et_respecte_le_nombre()
    {
        await using var magasin = CreerMagasin();
        var jeu = new JeuEssai(magasin.Entite);
        for (var i = 1; i <= 5; i++)
        {
            jeu.Ecriture(Guid.CreateVersion7(), _le10Mars, i, [Debit("1000", i), Credit("4000", i)]);
        }

        await magasin.AmorcerAsync(jeu);
        var premieres = await magasin.LignesCompte.LireLignesAsync(magasin.Entite, "1000", null, 2, Ct);
        var suivantes = await magasin.LignesCompte.LireLignesAsync(magasin.Entite, "1000", premieres[^1].Position, 2, Ct);
        var fin = await magasin.LignesCompte.LireLignesAsync(magasin.Entite, "1000", new PositionLigne(_le10Mars, 5, 1), 2, Ct);

        Assert.Equal([1L, 2L], premieres.Select(l => l.Sequence));
        Assert.Equal([3L, 4L], suivantes.Select(l => l.Sequence));
        Assert.Empty(fin);
        Assert.Empty(await magasin.LignesCompte.LireLignesAsync(magasin.AutreEntite, "1000", null, 2, Ct));
    }

    [Fact]
    public async Task Une_ecriture_est_lue_avec_ses_lignes_et_ses_liens_de_contre_passation()
    {
        await using var magasin = CreerMagasin();
        var origine = Guid.CreateVersion7();
        var inverse = Guid.CreateVersion7();
        await magasin.AmorcerAsync(new JeuEssai(magasin.Entite)
            .Ecriture(origine, _le10Mars, 3, [Debit("1000", 114.98m), Credit("4000", 100m), Credit("2310", 5m), Credit("2320", 9.98m)], "Vente", contrePasseePar: inverse)
            .Ecriture(inverse, _le20Mars, 8, [Credit("1000", 114.98m), Debit("4000", 100m), Debit("2310", 5m), Debit("2320", 9.98m)], "Contre-passation", contrePassationDe: origine));

        var lue = await magasin.Ecritures.TrouverEcritureAsync(origine, Ct);
        var contrePassation = await magasin.Ecritures.TrouverEcritureAsync(inverse, Ct);

        Assert.NotNull(lue);
        Assert.Equal((magasin.Entite, _le10Mars, Mars, "Vente", (Guid?)null, (Guid?)inverse, 3L, Origine.AddSeconds(3), Acteur), (lue.Entite, lue.DateAffaires, lue.Periode, lue.Libelle, lue.ContrePassationDe, lue.ContrePasseePar, lue.Sequence, lue.EnregistreeLe, lue.Acteur));
        Assert.Equal(
            [
                new LigneEcritureLue(1, "1000", "Debit", 114.98m, Devise),
                new LigneEcritureLue(2, "4000", "Credit", 100m, Devise),
                new LigneEcritureLue(3, "2310", "Credit", 5m, Devise),
                new LigneEcritureLue(4, "2320", "Credit", 9.98m, Devise),
            ],
            lue.Lignes);
        Assert.Equal(origine, contrePassation?.ContrePassationDe);
        Assert.Null(await magasin.Ecritures.TrouverEcritureAsync(Guid.CreateVersion7(), Ct));
    }

    [Fact]
    public async Task Une_periode_est_lue_avec_l_empreinte_de_son_sceau()
    {
        await using var magasin = CreerMagasin();
        var scelleeLe = Origine.AddDays(31);
        await magasin.AmorcerAsync(new JeuEssai(magasin.Entite)
            .Periode(Mars, "Sealed", 2, scelleeLe, 7, new string('a', 64))
            .Periode(Avril));

        var mars = await magasin.Periodes.TrouverPeriodeAsync(magasin.Entite, Mars, Ct);
        var avril = await magasin.Periodes.TrouverPeriodeAsync(magasin.Entite, Avril, Ct);

        Assert.Equal(new PeriodeLue(magasin.Entite, Mars, "Sealed", 2, Origine, scelleeLe, 7, new string('a', 64)), mars);
        Assert.Equal(new PeriodeLue(magasin.Entite, Avril, "Open", 1, Origine, null, null, null), avril);
        Assert.Null(await magasin.Periodes.TrouverPeriodeAsync(magasin.Entite, "2026-05", Ct));
        Assert.Null(await magasin.Periodes.TrouverPeriodeAsync(magasin.AutreEntite, Mars, Ct));
    }

    [Fact]
    public async Task L_instantane_d_une_periode_regroupe_ses_mouvements_tries()
    {
        await using var magasin = CreerMagasin();
        await magasin.AmorcerAsync(new JeuEssai(magasin.Entite).Instantane(
            Mars,
            42,
            6,
            new CumulCompte("4000", "CAD", 0m, 150m),
            new CumulCompte("1000", "USD", 20m, 0m),
            new CumulCompte("1000", "CAD", 150m, 0m)));

        var instantane = await magasin.Instantanes.TrouverInstantaneAsync(magasin.Entite, Mars, Ct);

        Assert.NotNull(instantane);
        Assert.Equal((42L, 6L), (instantane.SequenceSceau, instantane.NombreEvenements));
        Assert.Equal([("1000", "CAD"), ("1000", "USD"), ("4000", "CAD")], instantane.Mouvements.Select(m => (m.Compte, m.Devise)));
        Assert.Null(await magasin.Instantanes.TrouverInstantaneAsync(magasin.Entite, Avril, Ct));
    }

    [Fact]
    public async Task Les_taxes_d_une_periode_sont_lues_par_devise()
    {
        await using var magasin = CreerMagasin();
        await magasin.AmorcerAsync(new JeuEssai(magasin.Entite)
            .TaxesPeriode(Mars, "USD", 1m, 0m, 2m, 0m)
            .TaxesPeriode(Mars, "CAD", 5m, 2.5m, 9.98m, 4.99m)
            .TaxesPeriode(Avril, "CAD", 1m, 1m, 1m, 1m));

        var mars = await magasin.Taxes.LireTaxesAsync(magasin.Entite, Mars, Ct);

        Assert.Equal([new TaxesPeriode("CAD", 5m, 2.5m, 9.98m, 4.99m), new TaxesPeriode("USD", 1m, 0m, 2m, 0m)], mars);
        Assert.Empty(await magasin.Taxes.LireTaxesAsync(magasin.Entite, "2026-05", Ct));
    }

    [Fact]
    public async Task Les_reperes_de_fraicheur_donnent_la_plus_petite_position_et_le_premier_evenement_en_attente()
    {
        await using var magasin = CreerMagasin();
        var enRetard = "essai-" + Guid.CreateVersion7().ToString("N");
        var aJour = "essai-" + Guid.CreateVersion7().ToString("N");
        var premier = await magasin.EnregistrerEvenementAsync(Origine.AddMinutes(1));
        await magasin.EnregistrerEvenementAsync(Origine.AddMinutes(2));
        var dernier = await magasin.EnregistrerEvenementAsync(Origine.AddMinutes(3));
        await magasin.PositionnerAsync(enRetard, premier);
        await magasin.PositionnerAsync(aJour, dernier);

        var ensemble = await magasin.Reperes.LireAsync([enRetard, aJour], Ct);
        var seul = await magasin.Reperes.LireAsync([aJour], Ct);

        Assert.Equal(new ReperesFraicheur(premier, dernier, Origine.AddMinutes(2)), ensemble);
        Assert.Equal(new ReperesFraicheur(dernier, dernier, null), seul);
    }

    [Fact]
    public async Task Une_projection_sans_position_n_a_rien_vu()
    {
        await using var magasin = CreerMagasin();
        var dernier = await magasin.EnregistrerEvenementAsync(Origine);
        var jamaisVue = "essai-" + Guid.CreateVersion7().ToString("N");

        var reperes = await magasin.Reperes.LireAsync([jamaisVue], Ct);

        Assert.Equal(0, reperes.JusquaSequence);
        Assert.Equal(dernier, reperes.SequenceJournal);
        Assert.NotNull(reperes.PlusAncienEnAttente);
    }

    protected abstract MagasinLectureEssai CreerMagasin();

    /// <summary>Ventes en CAD (le 10 et le 20 mars) et en USD (le 5 avril).</summary>
    private static JeuEssai Ventes(string entite) =>
        new JeuEssai(entite)
            .Ecriture(Guid.CreateVersion7(), _le10Mars, 1, [Debit("1000", 100m), Credit("4000", 100m)])
            .Ecriture(Guid.CreateVersion7(), _le20Mars, 2, [Debit("1000", 50m), Credit("4000", 50m)])
            .Ecriture(Guid.CreateVersion7(), _le5Avril, 3, [Debit("1000", 20m, "USD"), Credit("4000", 20m, "USD")]);
}
