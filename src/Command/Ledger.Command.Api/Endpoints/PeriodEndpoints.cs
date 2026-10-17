using Ledger.Command.Api.Http;
using Ledger.Command.Application.Periods;
using Ledger.Contracts.Http.V1;
using Ledger.SharedKernel;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ledger.Command.Api.Endpoints;

/// <summary>Ouverture et scellement des périodes, avec la version attendue du flux de la période (ADR 0013 et 0014).</summary>
public static class PeriodEndpoints
{
    public static RouteGroupBuilder MapPeriods(this RouteGroupBuilder v1)
    {
        ArgumentNullException.ThrowIfNull(v1);
        var periods = v1.MapGroup("/periods").WithTags("Périodes");

        periods.MapPost("/{period}/open", OpenAsync)
            .WithName("OpenPeriod")
            .WithSummary("Ouvre une période (AAAA-MM)")
            .WithDescription("Une écriture n'est acceptée que dans une période ouverte. expectedVersion vaut 0 pour une période jamais ouverte.")
            .ProducesCommandProblems(StatusCodes.Status409Conflict);

        periods.MapPost("/{period}/seal", SealAsync)
            .WithName("SealPeriod")
            .WithSummary("Scelle une période (AAAA-MM)")
            .WithDescription("Fige la période : plus aucune écriture n'y est acceptée. Le sceau publie le nombre d'événements de la période et la racine de Merkle de leurs hash. expectedVersion vaut 1 pour une période ouverte.")
            .ProducesCommandProblems(StatusCodes.Status409Conflict);

        return v1;
    }

    public static Task<Results<Ok<PeriodResponse>, ValidationProblem, ProblemHttpResult>> OpenAsync(
        string period,
        PeriodCommandRequest request,
        OpenPeriodHandler handler,
        CommandLog log,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return HandleAsync("OpenPeriod", period, request, handler.HandleAsync, log, context, cancellationToken);
    }

    public static Task<Results<Ok<PeriodResponse>, ValidationProblem, ProblemHttpResult>> SealAsync(
        string period,
        PeriodCommandRequest request,
        SealPeriodHandler handler,
        CommandLog log,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(handler);
        return HandleAsync("SealPeriod", period, request, handler.HandleAsync, log, context, cancellationToken);
    }

    private static async Task<Results<Ok<PeriodResponse>, ValidationProblem, ProblemHttpResult>> HandleAsync(
        string commandName,
        string period,
        PeriodCommandRequest request,
        Func<PeriodCommand, Application.EventStore.EventMetadata, CancellationToken, Task<Result<PeriodReceipt>>> handle,
        CommandLog log,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(log);
        var errors = new RequestErrors();
        if (!RequestMapping.TryMap(period, request, errors, out var command))
        {
            return errors.ToProblem();
        }

        var result = await handle(command, CommandMetadata.From(context), cancellationToken).ConfigureAwait(false);
        if (result.IsFailure)
        {
            log.Rejected(commandName, result.Error.Code);
            return result.Error.ToProblem();
        }

        var receipt = result.Value;
        log.Accepted(commandName, receipt.GlobalSequence, replayed: false);
        return TypedResults.Ok(new PeriodResponse(
            receipt.Entity.Value,
            receipt.Period.ToString(),
            receipt.Status.ToString(),
            receipt.Version,
            receipt.GlobalSequence,
            receipt.RecordedAt,
            receipt.Hash,
            receipt.Digest?.EventCount,
            receipt.Digest?.MerkleRoot));
    }
}
