using Ledger.Command.Application.Entries;
using Ledger.Command.Application.EventStore;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Periods;
using Ledger.SharedKernel;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ledger.Command.Api.Http;

/// <summary>
/// Rejets de domaine en Problem Details (RFC 9457, ADR 0014) : <c>type</c> = <c>urn:ledger:problem:{code}</c>,
/// membres <c>code</c> et <c>details</c>. Un code absent de la table est une règle métier (<c>422</c>) ;
/// ajouter un code à un autre statut = ajouter une ligne.
/// </summary>
public static class DomainProblems
{
    public const string TypePrefix = "urn:ledger:problem:";

    private static readonly Dictionary<string, int> _statusByCode = new(StringComparer.Ordinal)
    {
        [EntryErrors.NotFoundCode] = StatusCodes.Status404NotFound,
        [EventStoreErrors.ConcurrencyConflictCode] = StatusCodes.Status409Conflict,
        [EventStoreErrors.WrongExpectedVersionCode] = StatusCodes.Status409Conflict,
        [EventStoreErrors.EntryIdAlreadyUsedCode] = StatusCodes.Status409Conflict,
        [EntryApplicationErrors.EntryIdConflictCode] = StatusCodes.Status409Conflict,
        [EntryErrors.AlreadyPostedCode] = StatusCodes.Status409Conflict,
        [EntryErrors.AlreadyReversedCode] = StatusCodes.Status409Conflict,
        [PeriodErrors.SealedCode] = StatusCodes.Status409Conflict,
        [PeriodErrors.NotOpenCode] = StatusCodes.Status409Conflict,
        [PeriodErrors.AlreadyOpenCode] = StatusCodes.Status409Conflict,
        [PeriodErrors.AlreadySealedCode] = StatusCodes.Status409Conflict,
    };

    public static int StatusFor(string code) => _statusByCode.GetValueOrDefault(code, StatusCodes.Status422UnprocessableEntity);

    public static ProblemHttpResult ToProblem(this DomainError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var status = StatusFor(error.Code);
        return TypedResults.Problem(
            detail: error.Message,
            statusCode: status,
            title: Title(status),
            type: TypePrefix + error.Code,
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["code"] = error.Code,
                ["details"] = error.Details ?? new Dictionary<string, string>(StringComparer.Ordinal),
            });
    }

    private static string Title(int status) => status switch
    {
        StatusCodes.Status404NotFound => "Ressource introuvable",
        StatusCodes.Status409Conflict => "Conflit avec l'état du grand livre",
        _ => "Règle métier non respectée",
    };
}
