using Ledger.SharedKernel;

namespace Ledger.Command.Domain;

/// <summary>Raccourci pour une décision acceptée.</summary>
internal static class Events
{
    public static Result<IReadOnlyList<IDomainEvent>> Of(params IDomainEvent[] events) =>
        Result.Success<IReadOnlyList<IDomainEvent>>(EquatableList.Of(events));
}
