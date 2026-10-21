using Ledger.Query.Application.Fraicheur;
using Ledger.Query.Application.Lecture;
using Ledger.SharedKernel;

namespace Ledger.Query.Application.Consultations;

/// <summary>Période, empreinte de son sceau et, une fois scellée, ses mouvements figés.</summary>
public sealed record PeriodeConsultee(PeriodeLue Periode, InstantanePeriode? Instantane, EtatFraicheur Fraicheur);

/// <summary>
/// Statut d'une période (<c>periods</c>) et son instantané de fin de période (<c>period_snapshots</c>,
/// ADR 0017). Une période scellée sans écriture n'a pas d'instantané.
/// </summary>
public sealed class ConsulterPeriode(ILecteurPeriodes periodes, ILecteurInstantanes instantanes, EvaluateurFraicheur fraicheur)
{
    public async Task<Result<PeriodeConsultee>> ExecuterAsync(string entite, string periode, CancellationToken cancellationToken)
    {
        var etat = await fraicheur.EvaluerAsync([NomsProjections.Periodes, NomsProjections.Instantanes], cancellationToken).ConfigureAwait(false);
        var lue = await periodes.TrouverPeriodeAsync(entite, periode, cancellationToken).ConfigureAwait(false);
        if (lue is null)
        {
            return Result.Failure<PeriodeConsultee>(ErreursConsultation.PeriodeIntrouvable(entite, periode));
        }

        var instantane = await instantanes.TrouverInstantaneAsync(entite, periode, cancellationToken).ConfigureAwait(false);
        return Result.Success(new PeriodeConsultee(lue, instantane, etat));
    }
}
