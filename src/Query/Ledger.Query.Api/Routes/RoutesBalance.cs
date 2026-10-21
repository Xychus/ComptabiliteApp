using Ledger.Contracts.Http.V1;
using Ledger.Query.Api.Http;
using Ledger.Query.Application.Consultations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Ledger.Query.Api.Routes;

/// <summary>Balance de vérification d'une entité (ADR 0019).</summary>
public static class RoutesBalance
{
    public static RouteGroupBuilder AssocierBalance(this RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        v1.MapGet("/trial-balance", BalanceAsync)
            .WithTags("Balance")
            .WithName("GetTrialBalance")
            .WithSummary("Balance de vérification")
            .WithDescription("Une ligne par compte mouvementé et devise, totaux par devise. Sans asOf : balance courante ; avec asOf (AAAA-MM-JJ) : lignes dont la date d'affaires est au plus asOf.")
            .ProduitProblemesLecture();
        return v1;
    }

    public static async Task<Results<Ok<TrialBalanceResponse>, ValidationProblem>> BalanceAsync(
        [FromQuery(Name = "entityId")] string? entite,
        [FromQuery(Name = "asOf")] string? date,
        ConsulterBalanceVerification consultation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consultation);
        var erreurs = new ErreursParametres();
        var entiteLue = ParametresRequete.Entite(entite, erreurs);
        ParametresRequete.Date(date, erreurs, out var auPlusTard);
        if (erreurs.Existe)
        {
            return erreurs.VersProbleme();
        }

        var balance = await consultation.ExecuterAsync(entiteLue!, auPlusTard, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(balance.VersContrat());
    }
}
