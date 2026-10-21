using Ledger.Contracts.Http.V1;
using Ledger.Query.Api.Http;
using Ledger.Query.Application.Consultations;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Ledger.Query.Api.Routes;

/// <summary>Solde et sous-livre d'un compte (ADR 0019).</summary>
public static class RoutesComptes
{
    public static RouteGroupBuilder AssocierComptes(this RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        var comptes = v1.MapGroup("/accounts").WithTags("Comptes");

        comptes.MapGet("/{accountId}/balance", SoldeAsync)
            .WithName("GetAccountBalance")
            .WithSummary("Solde d'un compte")
            .WithDescription("Sans asOf : solde courant. Avec asOf (AAAA-MM-JJ) : somme des lignes dont la date d'affaires est au plus asOf. Le solde est positif dans le sens normal du compte.")
            .ProduitProblemesLecture(StatusCodes.Status404NotFound);

        comptes.MapGet("/{accountId}/entries", LignesAsync)
            .WithName("GetAccountEntries")
            .WithSummary("Sous-livre d'un compte, paginé par curseur")
            .WithDescription($"Lignes dans l'ordre comptable (date d'affaires, séquence, ligne). limit : 1 à {ConsulterLignesCompte.TailleMaximale} (défaut {ConsulterLignesCompte.TailleParDefaut}). Passer nextCursor dans cursor pour la page suivante.")
            .ProduitProblemesLecture(StatusCodes.Status404NotFound);

        return v1;
    }

    public static async Task<Results<Ok<AccountBalanceResponse>, ValidationProblem, ProblemHttpResult>> SoldeAsync(
        string accountId,
        [FromQuery(Name = "entityId")] string? entite,
        [FromQuery(Name = "asOf")] string? date,
        ConsulterSoldeCompte consultation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consultation);
        var erreurs = new ErreursParametres();
        var compte = ParametresRequete.Compte(accountId, erreurs);
        var entiteLue = ParametresRequete.Entite(entite, erreurs);
        ParametresRequete.Date(date, erreurs, out var auPlusTard);
        if (erreurs.Existe)
        {
            return erreurs.VersProbleme();
        }

        var resultat = await consultation.ExecuterAsync(entiteLue!, compte!, auPlusTard, cancellationToken).ConfigureAwait(false);
        return resultat.IsSuccess ? TypedResults.Ok(resultat.Value.VersContrat()) : resultat.Error.VersProbleme();
    }

    public static async Task<Results<Ok<AccountEntriesResponse>, ValidationProblem, ProblemHttpResult>> LignesAsync(
        string accountId,
        [FromQuery(Name = "entityId")] string? entite,
        [FromQuery(Name = "cursor")] string? curseur,
        [FromQuery(Name = "limit")] int? taille,
        ConsulterLignesCompte consultation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(consultation);
        var erreurs = new ErreursParametres();
        var compte = ParametresRequete.Compte(accountId, erreurs);
        var entiteLue = ParametresRequete.Entite(entite, erreurs);
        var tailleLue = ParametresRequete.Taille(taille, erreurs);
        if (erreurs.Existe)
        {
            return erreurs.VersProbleme();
        }

        var resultat = await consultation.ExecuterAsync(entiteLue!, compte!, curseur, tailleLue, cancellationToken).ConfigureAwait(false);
        return resultat.IsSuccess ? TypedResults.Ok(resultat.Value.VersContrat()) : resultat.Error.VersProbleme();
    }
}
