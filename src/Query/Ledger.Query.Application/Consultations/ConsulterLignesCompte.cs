using Ledger.Query.Application.Fraicheur;
using Ledger.Query.Application.Lecture;
using Ledger.SharedKernel;

namespace Ledger.Query.Application.Consultations;

/// <summary>Page du sous-livre d'un compte ; <see cref="CurseurSuivant"/> est nul sur la dernière page.</summary>
public sealed record PageLignesCompte(string Entite, string Compte, IReadOnlyList<LigneCompte> Lignes, string? CurseurSuivant, EtatFraicheur Fraicheur);

/// <summary>
/// Sous-livre d'un compte, paginé par curseur dans l'ordre comptable (date d'affaires, séquence,
/// numéro de ligne). Une ligne de plus que la taille demandée est lue pour savoir s'il reste une page.
/// </summary>
public sealed class ConsulterLignesCompte(ILecteurPlanComptable planComptable, ILecteurLignesCompte lignes, EvaluateurFraicheur fraicheur)
{
    public const int TailleParDefaut = 100;
    public const int TailleMaximale = 500;

    public async Task<Result<PageLignesCompte>> ExecuterAsync(string entite, string compte, string? curseur, int taille, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(taille, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(taille, TailleMaximale);
        PositionLigne? apres = null;
        if (curseur is not null && !CurseurLignes.TryDecoder(curseur, out apres))
        {
            return Result.Failure<PageLignesCompte>(ErreursConsultation.CurseurInvalide());
        }

        var etat = await fraicheur.EvaluerAsync([NomsProjections.PlanComptable, NomsProjections.GrandLivre], cancellationToken).ConfigureAwait(false);
        if (await planComptable.TrouverCompteAsync(entite, compte, cancellationToken).ConfigureAwait(false) is null)
        {
            return Result.Failure<PageLignesCompte>(ErreursConsultation.CompteIntrouvable(entite, compte));
        }

        var lues = await lignes.LireLignesAsync(entite, compte, apres, taille + 1, cancellationToken).ConfigureAwait(false);
        var page = lues.Take(taille).ToList();
        var suivant = lues.Count > taille ? CurseurLignes.Encoder(page[^1].Position) : null;
        return Result.Success(new PageLignesCompte(entite, compte, page, suivant, etat));
    }
}
