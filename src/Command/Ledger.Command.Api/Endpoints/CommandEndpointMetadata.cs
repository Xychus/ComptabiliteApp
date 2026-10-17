using Ledger.Command.Api.Security;

namespace Ledger.Command.Api.Endpoints;

/// <summary>Réponses d'erreur communes aux commandes, déclarées dans le contrat OpenAPI.</summary>
public static class CommandEndpointMetadata
{
    /// <summary><c>401</c> et <c>403</c> (politique <see cref="LedgerAuthentication.WritePolicy"/>), plus les statuts de rejet de la commande.</summary>
    public static RouteHandlerBuilder ProducesCommandProblems(this RouteHandlerBuilder endpoint, params int[] rejectionStatuses)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(rejectionStatuses);
        endpoint.ProducesProblem(StatusCodes.Status401Unauthorized).ProducesProblem(StatusCodes.Status403Forbidden);
        foreach (var status in rejectionStatuses)
        {
            endpoint.ProducesProblem(status);
        }

        return endpoint;
    }
}

/// <summary>Journalisation structurée du résultat de chaque commande.</summary>
public sealed partial class CommandLog(ILogger<CommandLog> logger)
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Commande {Command} acceptée : séquence {SequenceNumber}, rejeu {Replayed}.")]
    public partial void Accepted(string command, long sequenceNumber, bool replayed);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Commande {Command} rejetée : {Code}.")]
    public partial void Rejected(string command, string code);
}
