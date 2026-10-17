namespace Ledger.Contracts.Http.V1;

// Contrats HTTP de l'API de commandes (ADR 0014). Les champs d'autorité serveur (séquence,
// horodatage, période, hash) n'apparaissent que dans les réponses : en entrée, ils sont ignorés.

/// <summary>Montant : chaîne décimale invariante (ex. <c>"114.98"</c>) et code de devise ISO 4217.</summary>
/// <param name="Amount">Montant en chaîne décimale, jamais un nombre JSON.</param>
/// <param name="Currency">Code ISO 4217 (ex. <c>CAD</c>).</param>
public sealed record MoneyDto(string Amount, string Currency);

/// <summary>Ligne d'écriture.</summary>
/// <param name="AccountId">Code du compte dans le plan comptable de l'entité.</param>
/// <param name="Side"><c>Debit</c> ou <c>Credit</c>.</param>
/// <param name="Amount">Montant strictement positif.</param>
public sealed record JournalLineDto(string AccountId, string Side, MoneyDto Amount);

/// <summary>Écriture à passer ; son identifiant (UUIDv7) est dans le chemin.</summary>
/// <param name="EntityId">Entité comptable qui tient le grand livre.</param>
/// <param name="BusinessDate">Date d'affaires (AAAA-MM-JJ) ; elle détermine la période.</param>
/// <param name="Lines">Au moins deux lignes ; débits = crédits, par devise.</param>
/// <param name="Description">Libellé facultatif (500 caractères au plus).</param>
public sealed record PostJournalEntryRequest(string EntityId, DateOnly BusinessDate, IReadOnlyList<JournalLineDto> Lines, string? Description = null);

/// <summary>Contre-passation de l'écriture désignée par le chemin.</summary>
/// <param name="ReversalEntryId">Identifiant (UUIDv7) de l'écriture inverse, choisi par le client ; clé d'idempotence.</param>
/// <param name="BusinessDate">Date d'affaires de la contre-passation.</param>
/// <param name="Description">Libellé facultatif (500 caractères au plus).</param>
public sealed record ReverseJournalEntryRequest(Guid ReversalEntryId, DateOnly BusinessDate, string? Description = null);

/// <summary>Accusé de passation ou de contre-passation.</summary>
/// <param name="EntryId">Identifiant de l'écriture créée (l'écriture inverse pour une contre-passation).</param>
/// <param name="SequenceNumber">Position dans le journal (autorité serveur).</param>
/// <param name="RecordedAt">Horodatage serveur (UTC, précision microseconde).</param>
/// <param name="Period">Période comptable (AAAA-MM).</param>
/// <param name="Hash">Hash SHA-256 de l'événement (hexadécimal).</param>
public sealed record JournalEntryResponse(Guid EntryId, long SequenceNumber, DateTimeOffset RecordedAt, string Period, string Hash);

/// <summary>Ouverture ou scellement d'une période ; la période (AAAA-MM) est dans le chemin.</summary>
/// <param name="EntityId">Entité comptable.</param>
/// <param name="ExpectedVersion">Version attendue du flux de la période (0 avant l'ouverture, 1 une fois ouverte).</param>
public sealed record PeriodCommandRequest(string EntityId, long ExpectedVersion);

/// <summary>Accusé d'ouverture ou de scellement d'une période.</summary>
/// <param name="EntityId">Entité comptable.</param>
/// <param name="Period">Période (AAAA-MM).</param>
/// <param name="Status"><c>Open</c> ou <c>Sealed</c>.</param>
/// <param name="Version">Nouvelle version du flux de la période.</param>
/// <param name="SequenceNumber">Position dans le journal.</param>
/// <param name="RecordedAt">Horodatage serveur.</param>
/// <param name="Hash">Hash de l'événement.</param>
/// <param name="EventCount">Scellement : nombre d'événements scellés.</param>
/// <param name="MerkleRoot">Scellement : racine de Merkle des hash scellés.</param>
public sealed record PeriodResponse(
    string EntityId,
    string Period,
    string Status,
    long Version,
    long SequenceNumber,
    DateTimeOffset RecordedAt,
    string Hash,
    long? EventCount,
    string? MerkleRoot);

/// <summary>Définition d'un compte du plan comptable ; son code est dans le chemin.</summary>
/// <param name="EntityId">Entité comptable.</param>
/// <param name="Name">Libellé (1 à 200 caractères).</param>
/// <param name="Currency">Devise du compte (immuable).</param>
/// <param name="NormalSide">Sens du solde positif, <c>Debit</c> ou <c>Credit</c> (immuable).</param>
/// <param name="IsActive">Un compte inactif refuse les nouvelles lignes.</param>
/// <param name="NoNegativeBalance">Le solde ne peut devenir négatif dans son sens normal (immuable).</param>
public sealed record DefineAccountRequest(string EntityId, string Name, string Currency, string NormalSide, bool IsActive, bool NoNegativeBalance);

/// <summary>Accusé de définition d'un compte : l'événement qui porte son état courant.</summary>
/// <param name="EntityId">Entité comptable.</param>
/// <param name="AccountId">Code du compte.</param>
/// <param name="ChartVersion">Version du flux du plan comptable de l'entité.</param>
/// <param name="SequenceNumber">Position dans le journal de l'événement qui définit l'état courant.</param>
/// <param name="RecordedAt">Horodatage serveur de cet événement.</param>
/// <param name="Hash">Hash de cet événement.</param>
public sealed record AccountResponse(string EntityId, string AccountId, long ChartVersion, long SequenceNumber, DateTimeOffset RecordedAt, string Hash);
