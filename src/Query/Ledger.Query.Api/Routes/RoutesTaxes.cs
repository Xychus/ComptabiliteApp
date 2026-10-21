using Ledger.Contracts.Http.V1;
using Ledger.Query.Api.Http;
using Ledger.Query.Application.Consultations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Ledger.Query.Api.Routes;

/// <summary>Base de la déclaration TPS/TVQ (ADR 0018 et 0019).</summary>
public static class RoutesTaxes
{
    public static RouteGroupBuilder AssocierTaxes(this RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        v1.MapGet("/tax/returns/gst-qst", DeclarationAsync)
            .WithTags("Taxes")
            .WithName("GetGstQstReturn")
            .WithSummary("TPS/TVQ perçues, payées et nettes d'une période")
            .WithDescription("Montants par devise de la période (AAAA-MM) ; net = perçue − payée. periodStatus indique si la période est scellée. Format à valider avec le comptable.")
            .ProduitProblemesLecture();
        return v1;
    }

    public static async Task<Results<Ok<GstQstReturnResponse>, ValidationProblem>> DeclarationAsync(
        [FromQuery(Name = "entityId")] string? entite,
        [FromQuery(Name = "period")] string? periode,
        ConsulterDeclarationTpsTvq consultation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consultation);
        var erreurs = new ErreursParametres();
        var entiteLue = ParametresRequete.Entite(entite, erreurs);
        var periodeLue = ParametresRequete.Periode(periode, erreurs);
        if (erreurs.Existe)
        {
            return erreurs.VersProbleme();
        }

        var declaration = await consultation.ExecuterAsync(entiteLue!, periodeLue!, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(declaration.VersContrat());
    }
}
