using Ledger.Command.Api.Http;
using Ledger.Command.Application.Accounts;
using Ledger.Contracts.Http.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ledger.Command.Api.Endpoints;

/// <summary>Plan comptable : définition et modification des comptes d'une entité (ADR 0012).</summary>
public static class AccountEndpoints
{
    public static RouteGroupBuilder MapAccounts(this RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        v1.MapGroup("/accounts").WithTags("Plan comptable")
            .MapPut("/{accountId}", DefineAsync)
            .WithName("DefineAccount")
            .WithSummary("Définit ou modifie un compte (idempotent)")
            .WithDescription("Crée le compte (201) ou change son nom et son état actif (200). La devise, le sens normal et noNegativeBalance sont immuables une fois le compte défini (422).")
            .ProducesCommandProblems(StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity);
        return v1;
    }

    public static async Task<Results<Created<AccountResponse>, Ok<AccountResponse>, ValidationProblem, ProblemHttpResult>> DefineAsync(
        string accountId,
        DefineAccountRequest request,
        DefineAccountHandler handler,
        CommandLog log,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(log);
        var errors = new RequestErrors();
        if (!RequestMapping.TryMap(accountId, request, errors, out var command))
        {
            return errors.ToProblem();
        }

        var result = await handler.HandleAsync(command, CommandMetadata.From(context), cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            log.Rejected("DefineAccount", result.Error.Code);
            return result.Error.ToProblem();
        }

        var receipt = result.Value;
        log.Accepted("DefineAccount", receipt.GlobalSequence, replayed: !receipt.Created);
        var response = new AccountResponse(receipt.Entity.Value, receipt.Account.Value, receipt.ChartVersion, receipt.GlobalSequence, receipt.RecordedAt, receipt.Hash);
        return receipt.Created
            ? TypedResults.Created($"/v1/accounts/{Uri.EscapeDataString(response.AccountId)}", response)
            : TypedResults.Ok(response);
    }
}
