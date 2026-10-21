using Ledger.Query.Application.Fraicheur;
using Ledger.Query.Application.Lecture;
using Ledger.SharedKernel;

namespace Ledger.Query.Application.Consultations;

/// <summary>Solde d'un compte dans une devise, positif dans son sens normal.</summary>
public sealed record SoldeDevise(string Devise, decimal Debits, decimal Credits, decimal Solde);

/// <summary>Solde d'un compte, courant ou à une date d'affaires.</summary>
public sealed record SoldeCompte(CompteLu Compte, DateOnly? AuPlusTard, IReadOnlyList<SoldeDevise> Soldes, EtatFraicheur Fraicheur);

/// <summary>
/// Solde d'un compte (ADR 0019) : sans date, les cumuls courants de <c>account_balances</c> ; avec
/// une date d'affaires, la somme des lignes du grand livre jusqu'à cette date incluse. Un compte
/// sans mouvement a un solde nul dans sa devise.
/// </summary>
public sealed class ConsulterSoldeCompte(
    ILecteurPlanComptable planComptable,
    ILecteurSoldes soldes,
    ILecteurMouvements mouvements,
    EvaluateurFraicheur fraicheur)
{
    public async Task<Result<SoldeCompte>> ExecuterAsync(string entite, string compte, DateOnly? auPlusTard, CancellationToken cancellationToken)
    {
        // Fraîcheur lue d'abord : les positions ne reculent jamais, les données lues ensuite sont au moins aussi récentes.
        var etat = await fraicheur.EvaluerAsync(
            auPlusTard is null ? [NomsProjections.PlanComptable, NomsProjections.Soldes] : [NomsProjections.PlanComptable, NomsProjections.GrandLivre],
            cancellationToken).ConfigureAwait(false);
        var lu = await planComptable.TrouverCompteAsync(entite, compte, cancellationToken).ConfigureAwait(false);
        if (lu is null)
        {
            return Result.Failure<SoldeCompte>(ErreursConsultation.CompteIntrouvable(entite, compte));
        }

        var cumuls = auPlusTard is { } date
            ? await mouvements.CumulerAsync(entite, compte, date, cancellationToken).ConfigureAwait(false)
            : await soldes.LireSoldesAsync(entite, compte, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<SoldeDevise> parDevise = cumuls.Count == 0
            ? [new SoldeDevise(lu.Devise, 0m, 0m, 0m)]
            : [.. cumuls.OrderBy(c => c.Devise, StringComparer.Ordinal).Select(c => new SoldeDevise(c.Devise, c.Debits, c.Credits, Signe(lu.SensNormal, c)))];
        return Result.Success(new SoldeCompte(lu, auPlusTard, parDevise, etat));
    }

    /// <summary>Solde positif dans le sens normal du compte : débits − crédits pour un compte débiteur, l'inverse sinon.</summary>
    public static decimal Signe(string sensNormal, CumulCompte cumul)
    {
        ArgumentNullException.ThrowIfNull(cumul);
        return sensNormal == SensComptable.Credit ? cumul.Credits - cumul.Debits : cumul.Debits - cumul.Credits;
    }
}

/// <summary>Sens d'une ligne ou sens normal d'un compte, tels qu'enregistrés.</summary>
public static class SensComptable
{
    public const string Debit = "Debit";
    public const string Credit = "Credit";
}
