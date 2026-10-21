namespace Ledger.Contracts.Http.V1;

// Contrats HTTP de l'API de requête (ADR 0019). Montants en chaînes décimales à deux décimales,
// toujours avec leur devise. Chaque réponse porte l'indicateur de fraîcheur des projections lues.

/// <summary>Fraîcheur des projections qui ont servi à la réponse.</summary>
/// <param name="ThroughSequence">Séquence du journal jusqu'à laquelle ces projections ont vu les événements.</param>
/// <param name="JournalSequence">Tête du journal au moment de la lecture.</param>
/// <param name="Stale">Vrai si un événement attend d'être projeté depuis plus que le seuil configuré.</param>
public sealed record FreshnessDto(long ThroughSequence, long JournalSequence, bool Stale);

/// <summary>Solde d'un compte dans une devise.</summary>
/// <param name="Currency">Code ISO 4217.</param>
/// <param name="Debits">Total des débits.</param>
/// <param name="Credits">Total des crédits.</param>
/// <param name="Balance">Solde, positif dans le sens normal du compte.</param>
public sealed record CurrencyBalanceDto(string Currency, string Debits, string Credits, string Balance);

/// <summary>Solde d'un compte, courant ou à une date d'affaires.</summary>
/// <param name="EntityId">Entité comptable.</param>
/// <param name="AccountId">Code du compte.</param>
/// <param name="Name">Libellé du compte.</param>
/// <param name="NormalSide"><c>Debit</c> ou <c>Credit</c>.</param>
/// <param name="AsOf">Date d'affaires incluse, ou absente pour le solde courant.</param>
/// <param name="Balances">Un solde par devise (au moins celui de la devise du compte).</param>
/// <param name="Freshness">Fraîcheur des projections lues.</param>
public sealed record AccountBalanceResponse(
    string EntityId,
    string AccountId,
    string Name,
    string NormalSide,
    DateOnly? AsOf,
    IReadOnlyList<CurrencyBalanceDto> Balances,
    FreshnessDto Freshness);

/// <summary>Ligne de la balance de vérification.</summary>
/// <param name="AccountId">Code du compte.</param>
/// <param name="Name">Libellé, absent si le compte n'est pas (encore) dans le plan comptable projeté.</param>
/// <param name="Currency">Code ISO 4217.</param>
/// <param name="Debits">Total des débits.</param>
/// <param name="Credits">Total des crédits.</param>
/// <param name="DebitBalance">Solde débiteur (débits − crédits s'il est positif, sinon 0).</param>
/// <param name="CreditBalance">Solde créditeur (crédits − débits s'il est positif, sinon 0).</param>
public sealed record TrialBalanceLineDto(string AccountId, string? Name, string Currency, string Debits, string Credits, string DebitBalance, string CreditBalance);

/// <summary>Totaux de la balance dans une devise.</summary>
/// <param name="Currency">Code ISO 4217.</param>
/// <param name="Debits">Total des débits.</param>
/// <param name="Credits">Total des crédits.</param>
/// <param name="DebitBalance">Total des soldes débiteurs.</param>
/// <param name="CreditBalance">Total des soldes créditeurs.</param>
/// <param name="Balanced">Débits = crédits (partie double).</param>
public sealed record TrialBalanceTotalDto(string Currency, string Debits, string Credits, string DebitBalance, string CreditBalance, bool Balanced);

/// <summary>Balance de vérification d'une entité.</summary>
/// <param name="EntityId">Entité comptable.</param>
/// <param name="AsOf">Date d'affaires incluse, ou absente pour la balance courante.</param>
/// <param name="Lines">Une ligne par compte mouvementé et devise, triées par code de compte.</param>
/// <param name="Totals">Totaux par devise.</param>
/// <param name="Balanced">Vrai si chaque devise est équilibrée.</param>
/// <param name="Freshness">Fraîcheur des projections lues.</param>
public sealed record TrialBalanceResponse(
    string EntityId,
    DateOnly? AsOf,
    IReadOnlyList<TrialBalanceLineDto> Lines,
    IReadOnlyList<TrialBalanceTotalDto> Totals,
    bool Balanced,
    FreshnessDto Freshness);

/// <summary>Ligne du sous-livre d'un compte.</summary>
/// <param name="EntryId">Écriture qui porte la ligne.</param>
/// <param name="LineNumber">Numéro de la ligne dans l'écriture.</param>
/// <param name="BusinessDate">Date d'affaires.</param>
/// <param name="Period">Période (AAAA-MM).</param>
/// <param name="Side"><c>Debit</c> ou <c>Credit</c>.</param>
/// <param name="Amount">Montant et devise.</param>
/// <param name="SequenceNumber">Position de l'écriture dans le journal.</param>
/// <param name="Description">Libellé de l'écriture.</param>
public sealed record AccountEntryLineDto(
    Guid EntryId,
    int LineNumber,
    DateOnly BusinessDate,
    string Period,
    string Side,
    MoneyDto Amount,
    long SequenceNumber,
    string? Description);

/// <summary>Page du sous-livre d'un compte, dans l'ordre comptable (date d'affaires, séquence, ligne).</summary>
/// <param name="EntityId">Entité comptable.</param>
/// <param name="AccountId">Code du compte.</param>
/// <param name="Items">Lignes de la page.</param>
/// <param name="NextCursor">Curseur de la page suivante (paramètre <c>cursor</c>), absent sur la dernière page.</param>
/// <param name="Freshness">Fraîcheur des projections lues.</param>
public sealed record AccountEntriesResponse(string EntityId, string AccountId, IReadOnlyList<AccountEntryLineDto> Items, string? NextCursor, FreshnessDto Freshness);

/// <summary>Ligne d'une écriture.</summary>
public sealed record JournalEntryLineDetailDto(int LineNumber, string AccountId, string Side, MoneyDto Amount);

/// <summary>Écriture du grand livre.</summary>
/// <param name="EntryId">Identifiant de l'écriture.</param>
/// <param name="EntityId">Entité comptable.</param>
/// <param name="BusinessDate">Date d'affaires.</param>
/// <param name="Period">Période (AAAA-MM).</param>
/// <param name="Description">Libellé.</param>
/// <param name="ReversalOf">Écriture d'origine, si celle-ci est une contre-passation.</param>
/// <param name="ReversedBy">Contre-passation qui annule cette écriture, le cas échéant.</param>
/// <param name="SequenceNumber">Position dans le journal.</param>
/// <param name="RecordedAt">Horodatage serveur.</param>
/// <param name="Actor">Client qui a passé l'écriture.</param>
/// <param name="Lines">Lignes, par numéro.</param>
/// <param name="Freshness">Fraîcheur des projections lues.</param>
public sealed record JournalEntryDetailResponse(
    Guid EntryId,
    string EntityId,
    DateOnly BusinessDate,
    string Period,
    string? Description,
    Guid? ReversalOf,
    Guid? ReversedBy,
    long SequenceNumber,
    DateTimeOffset RecordedAt,
    string Actor,
    IReadOnlyList<JournalEntryLineDetailDto> Lines,
    FreshnessDto Freshness);

/// <summary>Mouvements d'un compte dans une période scellée.</summary>
public sealed record PeriodMovementDto(string AccountId, string Currency, string Debits, string Credits);

/// <summary>Instantané de fin de période, figé au scellement.</summary>
/// <param name="SealSequenceNumber">Position du sceau dans le journal.</param>
/// <param name="EventCount">Nombre d'événements scellés.</param>
/// <param name="Movements">Mouvements par compte et devise.</param>
public sealed record PeriodSnapshotDto(long SealSequenceNumber, long EventCount, IReadOnlyList<PeriodMovementDto> Movements);

/// <summary>État d'une période.</summary>
/// <param name="EntityId">Entité comptable.</param>
/// <param name="Period">Période (AAAA-MM).</param>
/// <param name="Status"><c>Open</c> ou <c>Sealed</c>.</param>
/// <param name="Version">Version du flux de la période (valeur attendue par la commande suivante).</param>
/// <param name="OpenedAt">Horodatage de l'ouverture.</param>
/// <param name="SealedAt">Horodatage du scellement.</param>
/// <param name="EventCount">Scellement : nombre d'événements scellés.</param>
/// <param name="MerkleRoot">Scellement : racine de Merkle des hash scellés.</param>
/// <param name="Snapshot">Instantané, une fois la période scellée et projetée, si elle a des écritures.</param>
/// <param name="Freshness">Fraîcheur des projections lues.</param>
public sealed record PeriodDetailResponse(
    string EntityId,
    string Period,
    string Status,
    long Version,
    DateTimeOffset? OpenedAt,
    DateTimeOffset? SealedAt,
    long? EventCount,
    string? MerkleRoot,
    PeriodSnapshotDto? Snapshot,
    FreshnessDto Freshness);

/// <summary>Montants de TPS et de TVQ d'une période dans une devise ; net = perçue − payée.</summary>
public sealed record GstQstAmountsDto(
    string Currency,
    string GstCollected,
    string GstPaid,
    string GstNet,
    string QstCollected,
    string QstPaid,
    string QstNet);

/// <summary>Base de la déclaration TPS/TVQ d'une période (format à valider avec le comptable).</summary>
/// <param name="EntityId">Entité comptable.</param>
/// <param name="Period">Période de déclaration (AAAA-MM).</param>
/// <param name="PeriodStatus"><c>Open</c>, <c>Sealed</c>, ou absent si la période n'a jamais été ouverte.</param>
/// <param name="Amounts">Montants par devise ; vide si aucune taxe n'a été comptabilisée.</param>
/// <param name="Freshness">Fraîcheur des projections lues.</param>
public sealed record GstQstReturnResponse(string EntityId, string Period, string? PeriodStatus, IReadOnlyList<GstQstAmountsDto> Amounts, FreshnessDto Freshness);
