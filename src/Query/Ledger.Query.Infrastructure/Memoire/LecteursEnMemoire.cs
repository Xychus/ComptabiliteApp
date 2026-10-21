using Ledger.Query.Application.Lecture;

namespace Ledger.Query.Infrastructure.Memoire;

// Ports de lecture en mémoire : mêmes résultats et mêmes ordres que les requêtes PostgreSQL
// (tri ordinal, équivalent de COLLATE "C"). Vérifié par la suite de contrat commune.

public sealed class LecteurPlanComptableEnMemoire(DonneesLectureEnMemoire donnees) : ILecteurPlanComptable
{
    public Task<CompteLu?> TrouverCompteAsync(string entite, string compte, CancellationToken cancellationToken) =>
        Task.FromResult(donnees.Tables.Comptes.SingleOrDefault(c => c.Entite == entite && c.Compte == compte));

    public Task<IReadOnlyList<CompteLu>> LireComptesAsync(string entite, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CompteLu>>([.. donnees.Tables.Comptes.Where(c => c.Entite == entite).OrderBy(c => c.Compte, StringComparer.Ordinal)]);
}

public sealed class LecteurSoldesEnMemoire(DonneesLectureEnMemoire donnees) : ILecteurSoldes
{
    public Task<IReadOnlyList<CumulCompte>> LireSoldesAsync(string entite, string? compte, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CumulCompte>>(
        [
            .. donnees.Tables.Soldes
                .Where(s => s.Entite == entite && (compte is null || s.Compte == compte))
                .OrderBy(s => s.Compte, StringComparer.Ordinal)
                .ThenBy(s => s.Devise, StringComparer.Ordinal)
                .Select(s => new CumulCompte(s.Compte, s.Devise, s.Debits, s.Credits)),
        ]);
}

public sealed class LecteurMouvementsEnMemoire(DonneesLectureEnMemoire donnees) : ILecteurMouvements
{
    public Task<IReadOnlyList<CumulCompte>> CumulerAsync(string entite, string? compte, DateOnly auPlusTard, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CumulCompte>>(
        [
            .. donnees.Tables.Lignes
                .Where(l => l.Entite == entite && (compte is null || l.Compte == compte) && l.DateAffaires <= auPlusTard)
                .GroupBy(l => (l.Compte, l.Devise))
                .OrderBy(g => g.Key.Compte, StringComparer.Ordinal)
                .ThenBy(g => g.Key.Devise, StringComparer.Ordinal)
                .Select(g => new CumulCompte(
                    g.Key.Compte,
                    g.Key.Devise,
                    g.Where(l => l.Sens == "Debit").Sum(l => l.Montant),
                    g.Where(l => l.Sens == "Credit").Sum(l => l.Montant))),
        ]);
}

public sealed class LecteurLignesCompteEnMemoire(DonneesLectureEnMemoire donnees) : ILecteurLignesCompte
{
    public Task<IReadOnlyList<LigneCompte>> LireLignesAsync(string entite, string compte, PositionLigne? apres, int nombre, CancellationToken cancellationToken)
    {
        var tables = donnees.Tables;
        var libelles = tables.Ecritures.ToDictionary(e => e.Ecriture, e => e.Libelle);
        return Task.FromResult<IReadOnlyList<LigneCompte>>(
        [
            .. tables.Lignes
                .Where(l => l.Entite == entite && l.Compte == compte)
                .Select(l => new LigneCompte(l.Ecriture, l.NumeroLigne, l.DateAffaires, l.Periode, l.Sens, l.Montant, l.Devise, l.Sequence, libelles[l.Ecriture]))
                .Where(l => apres is null || EstApres(l.Position, apres))
                .OrderBy(l => l.DateAffaires)
                .ThenBy(l => l.Sequence)
                .ThenBy(l => l.NumeroLigne)
                .Take(nombre),
        ]);
    }

    private static bool EstApres(PositionLigne position, PositionLigne reference) =>
        (position.DateAffaires, position.Sequence, position.NumeroLigne).CompareTo((reference.DateAffaires, reference.Sequence, reference.NumeroLigne)) > 0;
}

public sealed class LecteurEcrituresEnMemoire(DonneesLectureEnMemoire donnees) : ILecteurEcritures
{
    public Task<EcritureLue?> TrouverEcritureAsync(Guid ecriture, CancellationToken cancellationToken)
    {
        var tables = donnees.Tables;
        var e = tables.Ecritures.SingleOrDefault(e => e.Ecriture == ecriture);
        return Task.FromResult(e is null
            ? null
            : new EcritureLue(
                e.Ecriture,
                e.Entite,
                e.DateAffaires,
                e.Periode,
                e.Libelle,
                e.ContrePassationDe,
                e.ContrePasseePar,
                e.Sequence,
                e.EnregistreeLe,
                e.Acteur,
                [.. tables.Lignes.Where(l => l.Ecriture == ecriture).OrderBy(l => l.NumeroLigne).Select(l => new LigneEcritureLue(l.NumeroLigne, l.Compte, l.Sens, l.Montant, l.Devise))]));
    }
}

public sealed class LecteurPeriodesEnMemoire(DonneesLectureEnMemoire donnees) : ILecteurPeriodes
{
    public Task<PeriodeLue?> TrouverPeriodeAsync(string entite, string periode, CancellationToken cancellationToken) =>
        Task.FromResult(donnees.Tables.Periodes.SingleOrDefault(p => p.Entite == entite && p.Periode == periode));
}

public sealed class LecteurInstantanesEnMemoire(DonneesLectureEnMemoire donnees) : ILecteurInstantanes
{
    public Task<InstantanePeriode?> TrouverInstantaneAsync(string entite, string periode, CancellationToken cancellationToken)
    {
        var lignes = donnees.Tables.Instantanes
            .Where(i => i.Entite == entite && i.Periode == periode)
            .OrderBy(i => i.Compte, StringComparer.Ordinal)
            .ThenBy(i => i.Devise, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(lignes.Count == 0
            ? null
            : new InstantanePeriode(lignes[0].SequenceSceau, lignes[0].NombreEvenements, [.. lignes.Select(i => new CumulCompte(i.Compte, i.Devise, i.Debits, i.Credits))]));
    }
}

public sealed class LecteurTaxesEnMemoire(DonneesLectureEnMemoire donnees) : ILecteurTaxes
{
    public Task<IReadOnlyList<TaxesPeriode>> LireTaxesAsync(string entite, string periode, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<TaxesPeriode>>(
        [
            .. donnees.Tables.Taxes
                .Where(t => t.Entite == entite && t.Periode == periode)
                .OrderBy(t => t.Devise, StringComparer.Ordinal)
                .Select(t => new TaxesPeriode(t.Devise, t.TpsPercue, t.TpsPayee, t.TvqPercue, t.TvqPayee)),
        ]);
}

public sealed class LecteurReperesFraicheurEnMemoire(DonneesLectureEnMemoire donnees) : ILecteurReperesFraicheur
{
    public Task<ReperesFraicheur> LireAsync(IReadOnlyCollection<string> projections, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projections);
        var tables = donnees.Tables;
        var jusqua = projections.Count == 0 ? 0 : projections.Min(p => tables.Positions.GetValueOrDefault(p));
        var tete = tables.Journal.Count == 0 ? 0 : tables.Journal.Max(e => e.Sequence);
        var enAttente = tables.Journal.Where(e => e.Sequence > jusqua).OrderBy(e => e.Sequence).FirstOrDefault();
        return Task.FromResult(new ReperesFraicheur(jusqua, tete, enAttente?.EnregistreLe));
    }
}
