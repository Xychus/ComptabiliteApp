using System.Globalization;
using Ledger.Command.Domain.Periods;
using Ledger.SharedKernel;

namespace Ledger.Command.Application.EventStore;

/// <summary>Rejets de l'event store, communs à toutes ses implémentations.</summary>
public static class EventStoreErrors
{
    /// <summary>Une version visée est déjà prise, ou ne suit pas la version courante du flux.</summary>
    public const string ConcurrencyConflictCode = "ConcurrencyConflict";

    /// <summary>La version du flux ne correspond pas à celle que le client attendait.</summary>
    public const string WrongExpectedVersionCode = "WrongExpectedVersion";

    /// <summary>L'identifiant d'écriture est déjà réservé par un autre événement (ADR 0003).</summary>
    public const string EntryIdAlreadyUsedCode = "EntryIdAlreadyUsed";

    /// <summary>Même code que le rejet du domaine : la période est scellée (ADR 0005).</summary>
    public const string PeriodSealedCode = PeriodErrors.SealedCode;

    public static DomainError ConcurrencyConflict(string streamId, long streamVersion) =>
        DomainError.WithDetails(
            ConcurrencyConflictCode,
            $"La version {streamVersion.ToString(CultureInfo.InvariantCulture)} du flux {streamId} n'est pas disponible.",
            ("streamId", streamId),
            ("streamVersion", streamVersion.ToString(CultureInfo.InvariantCulture)));

    /// <summary>Conflit détecté à la validation, sans que le flux en cause soit connu (contrainte d'unicité, interblocage).</summary>
    public static DomainError ConcurrencyConflict() =>
        new(ConcurrencyConflictCode, "Une écriture concurrente a pris une des versions visées.");

    public static DomainError WrongExpectedVersion(string streamId, long expected, long actual) =>
        DomainError.WithDetails(
            WrongExpectedVersionCode,
            $"Le flux {streamId} est en version {actual.ToString(CultureInfo.InvariantCulture)}, version attendue {expected.ToString(CultureInfo.InvariantCulture)}.",
            ("streamId", streamId),
            ("expectedVersion", expected.ToString(CultureInfo.InvariantCulture)),
            ("actualVersion", actual.ToString(CultureInfo.InvariantCulture)));

    public static DomainError EntryIdAlreadyUsed(Guid entryId) =>
        DomainError.WithDetails(EntryIdAlreadyUsedCode, $"L'identifiant d'écriture {entryId} est déjà utilisé.", ("entryId", entryId.ToString()));

    public static DomainError PeriodSealed(string entityId, string period) =>
        DomainError.WithDetails(PeriodSealedCode, $"La période {period} est scellée.", ("entity", entityId), ("period", period));
}
