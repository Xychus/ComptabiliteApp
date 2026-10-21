using Ledger.Query.Application.Fraicheur;
using Ledger.Query.Application.Lecture;
using Ledger.SharedKernel;

namespace Ledger.Query.Application.Consultations;

public sealed record EcritureConsultee(EcritureLue Ecriture, EtatFraicheur Fraicheur);

/// <summary>Une écriture du grand livre et ses lignes, avec ses liens de contre-passation.</summary>
public sealed class ConsulterEcriture(ILecteurEcritures ecritures, EvaluateurFraicheur fraicheur)
{
    public async Task<Result<EcritureConsultee>> ExecuterAsync(Guid ecriture, CancellationToken cancellationToken)
    {
        var etat = await fraicheur.EvaluerAsync([NomsProjections.GrandLivre], cancellationToken).ConfigureAwait(false);
        var lue = await ecritures.TrouverEcritureAsync(ecriture, cancellationToken).ConfigureAwait(false);
        return lue is null
            ? Result.Failure<EcritureConsultee>(ErreursConsultation.EcritureIntrouvable(ecriture))
            : Result.Success(new EcritureConsultee(lue, etat));
    }
}
