using Ledger.Command.Api.Http;
using Ledger.Command.Application.Entries;
using Ledger.Contracts.Http.V1;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ledger.Command.Api.Endpoints;

/// <summary>Passation et contre-passation d'écritures (ADR 0003 et 0014). Aucune logique métier : HTTP ↔ commandes.</summary>
public static class JournalEntryEndpoints
{
    public static RouteGroupBuilder MapJournalEntries(this RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        var entries = v1.MapGroup("/journal-entries").WithTags("Écritures");

        entries.MapPut("/{entryId}", PostAsync)
            .WithName("PostJournalEntry")
            .WithSummary("Passe une écriture (idempotent)")
            .WithDescription("Crée l'écriture identifiée par entryId (UUIDv7 choisi par le client). Rejouer la même requête renvoie l'écriture existante (200) ; réutiliser l'identifiant pour un autre contenu est un conflit (409).")
            .ProducesCommandProblems(StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity);

        entries.MapPost("/{entryId}/reversals", ReverseAsync)
            .WithName("ReverseJournalEntry")
            .WithSummary("Contre-passe une écriture (idempotent)")
            .WithDescription("Passe l'écriture inverse de entryId, identifiée par reversalEntryId. Rejouer la même contre-passation renvoie l'existante (200) ; une écriture ne se contre-passe qu'une fois (409).")
            .ProducesCommandProblems(StatusCodes.Status404NotFound, StatusCodes.Status409Conflict, StatusCodes.Status422UnprocessableEntity);

        return v1;
    }

    public static async Task<Results<Created<JournalEntryResponse>, Ok<JournalEntryResponse>, ValidationProblem, ProblemHttpResult>> PostAsync(
        Guid entryId,
        PostJournalEntryRequest request,
        PostJournalEntryHandler handler,
        CommandLog log,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(log);
        var errors = new RequestErrors();
        if (!RequestMapping.TryMap(entryId, request, errors, out var command))
        {
            return errors.ToProblem();
        }

        var result = await handler.HandleAsync(command, CommandMetadata.From(context), cancellationToken).ConfigureAwait(false);
        return Respond("PostJournalEntry", result, log);
    }

    public static async Task<Results<Created<JournalEntryResponse>, Ok<JournalEntryResponse>, ValidationProblem, ProblemHttpResult>> ReverseAsync(
        Guid entryId,
        ReverseJournalEntryRequest request,
        ReverseJournalEntryHandler handler,
        CommandLog log,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        ArgumentNullException.ThrowIfNull(log);
        var errors = new RequestErrors();
        if (!RequestMapping.TryMap(entryId, request, errors, out var command))
        {
            return errors.ToProblem();
        }

        var result = await handler.HandleAsync(command, CommandMetadata.From(context), cancellationToken).ConfigureAwait(false);
        return Respond("ReverseJournalEntry", result, log);
    }

    private static Results<Created<JournalEntryResponse>, Ok<JournalEntryResponse>, ValidationProblem, ProblemHttpResult> Respond(
        string commandName,
        SharedKernel.Result<EntryReceipt> result,
        CommandLog log)
    {
        if (result.IsFailure)
        {
            log.Rejected(commandName, result.Error.Code);
            return result.Error.ToProblem();
        }

        var receipt = result.Value;
        log.Accepted(commandName, receipt.GlobalSequence, receipt.AlreadyExisted);
        var response = new JournalEntryResponse(receipt.EntryId.Value, receipt.GlobalSequence, receipt.RecordedAt, receipt.Period, receipt.Hash);
        return receipt.AlreadyExisted
            ? TypedResults.Ok(response)
            : TypedResults.Created($"/v1/journal-entries/{response.EntryId:D}", response);
    }
}
