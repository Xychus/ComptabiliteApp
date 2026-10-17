namespace Ledger.Contracts.Events.V1;

/// <summary>
/// Version de schéma des contrats d'événements de ce namespace. Indépendante de la version SemVer
/// du composant : un changement de schéma ajoute une V2 et un upcaster, la V1 ne change jamais.
/// </summary>
public static class EventSchema
{
    public const int Version = 1;
}

/// <summary>Noms stables des types d'événements (colonne <c>event_type</c>).</summary>
public static class EventTypes
{
    public const string JournalEntryPosted = "JournalEntryPosted";
    public const string JournalEntryReversed = "JournalEntryReversed";
    public const string AccountLinePosted = "AccountLinePosted";
    public const string PeriodOpened = "PeriodOpened";
    public const string PeriodSealed = "PeriodSealed";
    public const string AccountDefined = "AccountDefined";
}

/// <summary>Montant en chaîne décimale invariante (ex. <c>"114.98"</c>) et code de devise ISO 4217.</summary>
public sealed record MoneyV1(string Amount, string Currency);

/// <summary>Ligne d'écriture ; <see cref="Side"/> vaut <c>"Debit"</c> ou <c>"Credit"</c>.</summary>
public sealed record JournalLineV1(string AccountId, string Side, MoneyV1 Amount);

public sealed record JournalEntryPostedV1(
    Guid EntryId,
    string EntityId,
    DateOnly BusinessDate,
    string Period,
    string? Description,
    IReadOnlyList<JournalLineV1> Lines)
{
    public int SchemaVersion { get; init; } = EventSchema.Version;
}

public sealed record JournalEntryReversedV1(
    Guid ReversalEntryId,
    Guid ReversalOf,
    string EntityId,
    DateOnly BusinessDate,
    string Period,
    string? Description,
    IReadOnlyList<JournalLineV1> Lines)
{
    public int SchemaVersion { get; init; } = EventSchema.Version;
}

public sealed record AccountLinePostedV1(
    string EntityId,
    string AccountId,
    Guid SourceEntryId,
    string Side,
    MoneyV1 Amount,
    MoneyV1 BalanceAfter)
{
    public int SchemaVersion { get; init; } = EventSchema.Version;
}

public sealed record PeriodOpenedV1(string EntityId, string Period)
{
    public int SchemaVersion { get; init; } = EventSchema.Version;
}

/// <summary>
/// Sceau de période (ADR 0005 et 0013). <see cref="EventCount"/> et <see cref="MerkleRoot"/> décrivent
/// les événements de l'entité dans la période, hors le sceau ; le nom <c>eventCount</c> est lu par
/// l'event store.
/// </summary>
public sealed record PeriodSealedV1(string EntityId, string Period)
{
    public int SchemaVersion { get; init; } = EventSchema.Version;

    public long EventCount { get; init; }

    public string? MerkleRoot { get; init; }
}

/// <summary>État complet d'un compte du plan comptable (ADR 0012) ; <see cref="NormalSide"/> vaut <c>"Debit"</c> ou <c>"Credit"</c>.</summary>
public sealed record AccountDefinedV1(
    string EntityId,
    string AccountId,
    string Name,
    string Currency,
    string NormalSide,
    bool IsActive,
    bool NoNegativeBalance)
{
    public int SchemaVersion { get; init; } = EventSchema.Version;
}
