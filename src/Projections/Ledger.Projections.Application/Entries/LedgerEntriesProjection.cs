using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Events;
using Ledger.Projections.Application.Processing;

namespace Ledger.Projections.Application.Entries;

/// <summary>Ligne du grand livre ; <see cref="LineNumber"/> commence à 1, dans l'ordre de l'écriture.</summary>
public sealed record LedgerLine(int LineNumber, string AccountId, string Side, decimal Amount, string Currency);

/// <summary>Écriture du grand livre ; <see cref="ReversalOf"/> désigne l'écriture qu'une contre-passation annule.</summary>
public sealed record LedgerEntry(
    Guid EntryId,
    string EntityId,
    DateOnly BusinessDate,
    string Period,
    string? Description,
    Guid? ReversalOf,
    long GlobalSequence,
    DateTimeOffset RecordedAt,
    string Actor,
    IReadOnlyList<LedgerLine> Lines);

/// <summary>Port d'écriture de <c>journal_entries</c> et <c>ledger_lines</c>.</summary>
public interface ILedgerEntryStore
{
    Task AddAsync(LedgerEntry entry, CancellationToken cancellationToken);

    /// <summary>Note que <paramref name="entryId"/> a été contre-passée par <paramref name="reversalEntryId"/>.</summary>
    Task MarkReversedAsync(Guid entryId, Guid reversalEntryId, CancellationToken cancellationToken);
}

/// <summary>
/// Grand livre : chaque écriture avec ses lignes, consultable par compte (sous-livre d'un compte)
/// dans l'ordre comptable (date d'affaires, puis séquence du journal). Une contre-passation est
/// une écriture à part entière, liée à l'écriture d'origine.
/// </summary>
public sealed class LedgerEntriesProjection(ILedgerEntryStore store)
    : IProjection, IEventHandler<JournalEntryPostedV1>, IEventHandler<JournalEntryReversedV1>
{
    public string Name => ProjectionNames.LedgerEntries;

    public Task HandleAsync(ProjectedEvent<JournalEntryPostedV1> projectedEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectedEvent);
        var data = projectedEvent.Data;
        return store.AddAsync(ToEntry(data.EntryId, data.EntityId, data.BusinessDate, data.Period, data.Description, null, data.Lines, projectedEvent.Context), cancellationToken);
    }

    public async Task HandleAsync(ProjectedEvent<JournalEntryReversedV1> projectedEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectedEvent);
        var data = projectedEvent.Data;
        var reversal = ToEntry(data.ReversalEntryId, data.EntityId, data.BusinessDate, data.Period, data.Description, data.ReversalOf, data.Lines, projectedEvent.Context);
        await store.AddAsync(reversal, cancellationToken).ConfigureAwait(false);
        await store.MarkReversedAsync(data.ReversalOf, data.ReversalEntryId, cancellationToken).ConfigureAwait(false);
    }

    private static LedgerEntry ToEntry(
        Guid entryId,
        string entityId,
        DateOnly businessDate,
        string period,
        string? description,
        Guid? reversalOf,
        IReadOnlyList<JournalLineV1> lines,
        EventContext context) =>
        new(
            entryId,
            entityId,
            businessDate,
            period,
            description,
            reversalOf,
            context.GlobalSequence,
            context.RecordedAt,
            context.Actor,
            [.. lines.Select((line, index) => new LedgerLine(index + 1, line.AccountId, ToSide(line.Side), JournalLines.ParseAmount(line.Amount), line.Amount.Currency))]);

    private static string ToSide(string side) => JournalLines.IsDebit(side) ? JournalLines.Debit : JournalLines.Credit;
}
