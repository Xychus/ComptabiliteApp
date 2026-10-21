using Ledger.Query.Application.Fraicheur;
using Ledger.Query.Application.Lecture;

namespace Ledger.Query.Application.Consultations;

/// <summary>Ligne de la balance : cumuls du compte et solde dans la colonne débitrice ou créditrice.</summary>
public sealed record LigneBalance(string Compte, string? Nom, string Devise, decimal Debits, decimal Credits, decimal SoldeDebiteur, decimal SoldeCrediteur);

/// <summary>Totaux de la balance dans une devise.</summary>
public sealed record TotalBalance(string Devise, decimal Debits, decimal Credits, decimal SoldeDebiteur, decimal SoldeCrediteur)
{
    /// <summary>Partie double : total des débits = total des crédits, donc soldes débiteurs = soldes créditeurs.</summary>
    public bool Equilibree => Debits == Credits && SoldeDebiteur == SoldeCrediteur;
}

/// <summary>Balance de vérification d'une entité, courante ou à une date d'affaires.</summary>
public sealed record BalanceVerification(string Entite, DateOnly? AuPlusTard, IReadOnlyList<LigneBalance> Lignes, IReadOnlyList<TotalBalance> Totaux, EtatFraicheur Fraicheur)
{
    public bool Equilibree => Totaux.All(total => total.Equilibree);
}

/// <summary>
/// Balance de vérification (ADR 0019) : une ligne par compte mouvementé et devise, triée par code
/// de compte, avec les totaux par devise. Mêmes sources que le solde d'un compte.
/// </summary>
public sealed class ConsulterBalanceVerification(
    ILecteurPlanComptable planComptable,
    ILecteurSoldes soldes,
    ILecteurMouvements mouvements,
    EvaluateurFraicheur fraicheur)
{
    public async Task<BalanceVerification> ExecuterAsync(string entite, DateOnly? auPlusTard, CancellationToken cancellationToken)
    {
        var etat = await fraicheur.EvaluerAsync(
            auPlusTard is null ? [NomsProjections.PlanComptable, NomsProjections.Soldes] : [NomsProjections.PlanComptable, NomsProjections.GrandLivre],
            cancellationToken).ConfigureAwait(false);
        var noms = (await planComptable.LireComptesAsync(entite, cancellationToken).ConfigureAwait(false))
            .ToDictionary(c => c.Compte, c => c.Nom, StringComparer.Ordinal);
        var cumuls = auPlusTard is { } date
            ? await mouvements.CumulerAsync(entite, null, date, cancellationToken).ConfigureAwait(false)
            : await soldes.LireSoldesAsync(entite, null, cancellationToken).ConfigureAwait(false);

        var lignes = cumuls
            .OrderBy(c => c.Compte, StringComparer.Ordinal)
            .ThenBy(c => c.Devise, StringComparer.Ordinal)
            .Select(c => new LigneBalance(
                c.Compte,
                noms.GetValueOrDefault(c.Compte),
                c.Devise,
                c.Debits,
                c.Credits,
                Math.Max(c.Debits - c.Credits, 0m),
                Math.Max(c.Credits - c.Debits, 0m)))
            .ToList();
        var totaux = lignes
            .GroupBy(l => l.Devise, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new TotalBalance(g.Key, g.Sum(l => l.Debits), g.Sum(l => l.Credits), g.Sum(l => l.SoldeDebiteur), g.Sum(l => l.SoldeCrediteur)))
            .ToList();
        return new BalanceVerification(entite, auPlusTard, lignes, totaux, etat);
    }
}
