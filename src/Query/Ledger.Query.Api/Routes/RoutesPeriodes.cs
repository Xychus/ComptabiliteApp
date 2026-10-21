using Ledger.Contracts.Http.V1;
using Ledger.Query.Api.Http;
using Ledger.Query.Application.Consultations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Ledger.Query.Api.Routes;

/// <summary>État d'une période et instantané de fin de période (ADR 0017 et 0019).</summary>
public static class RoutesPeriodes
{
    public static RouteGroupBuilder AssocierPeriodes(this RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        v1.MapGet("/periods/{period}", PeriodeAsync)
            .WithTags("Périodes")
            .WithName("GetPeriod")
            .WithSummary("État d'une période (AAAA-MM)")
            .WithDescription("Statut, version du flux (valeur attendue par la commande suivante), empreinte du sceau et, une fois scellée, les mouvements figés de la période.")
            .ProduitProblemesLecture(StatusCodes.Status404NotFound);
        return v1;
    }

    public static async Task<Results<Ok<PeriodDetailResponse>, ValidationProblem, ProblemHttpResult>> PeriodeAsync(
        string period,
        [FromQuery(Name = "entityId")] string? entite,
        ConsulterPeriode consultation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consultation);
        var erreurs = new ErreursParametres();
        var periode = ParametresRequete.Periode(period, erreurs);
        var entiteLue = ParametresRequete.Entite(entite, erreurs);
        if (erreurs.Existe)
        {
            return erreurs.VersProbleme();
        }

        var resultat = await consultation.ExecuterAsync(entiteLue!, periode!, cancellationToken).ConfigureAwait(false);
        return resultat.IsSuccess ? TypedResults.Ok(resultat.Value.VersContrat()) : resultat.Error.VersProbleme();
    }
}
