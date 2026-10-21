using Ledger.Contracts.Http.V1;
using Ledger.Query.Api.Http;
using Ledger.Query.Application.Consultations;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ledger.Query.Api.Routes;

/// <summary>Consultation d'une écriture du grand livre (ADR 0019).</summary>
public static class RoutesEcritures
{
    public static RouteGroupBuilder AssocierEcritures(this RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        v1.MapGet("/journal-entries/{entryId}", EcritureAsync)
            .WithTags("Écritures")
            .WithName("GetJournalEntry")
            .WithSummary("Écriture du grand livre")
            .WithDescription("Lignes de l'écriture, liens de contre-passation (reversalOf, reversedBy) et champs d'autorité serveur.")
            .ProduitProblemesLecture(StatusCodes.Status404NotFound);
        return v1;
    }

    public static async Task<Results<Ok<JournalEntryDetailResponse>, ValidationProblem, ProblemHttpResult>> EcritureAsync(
        string entryId,
        ConsulterEcriture consultation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consultation);
        var erreurs = new ErreursParametres();
        var ecriture = ParametresRequete.Ecriture(entryId, erreurs);
        if (erreurs.Existe)
        {
            return erreurs.VersProbleme();
        }

        var resultat = await consultation.ExecuterAsync(ecriture!.Value, cancellationToken).ConfigureAwait(false);
        return resultat.IsSuccess ? TypedResults.Ok(resultat.Value.VersContrat()) : resultat.Error.VersProbleme();
    }
}
