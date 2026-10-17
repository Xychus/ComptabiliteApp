using Ledger.Command.Application.Accounts;
using Ledger.Command.Application.Events;
using Ledger.Command.Application.EventStore;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Periods;
using Ledger.Contracts.Events.V1;
using Ledger.SharedKernel;

namespace Ledger.Command.Application.Entries;

/// <summary>
/// Contre-passe une écriture : <c>JournalEntryReversed</c> dans le flux de l'écriture d'origine
/// (réservant <c>reversalEntryId</c>, ADR 0003) et un <c>AccountLinePosted</c> par ligne inversée de
/// compte contraint (ADR 0004), dans une seule transaction. Le rejeu de la même contre-passation
/// renvoie l'existant.
/// </summary>
public sealed class ReverseJournalEntryHandler(
    AggregateStreamReader streams,
    IEventAppender appender,
    IUnitOfWork unitOfWork,
    IChartOfAccountsReader charts,
    EventCatalog catalog,
    ConcurrencyRetryPolicy retryPolicy)
{
    private readonly ConstrainedAccountLines _constrainedLines = new(streams);

    public Task<Result<EntryReceipt>> HandleAsync(ReverseEntry command, EventMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(metadata);
        return retryPolicy.RunAsync(() => TryOnceAsync(command, metadata, cancellationToken));
    }

    private async Task<Result<EntryReceipt>> TryOnceAsync(ReverseEntry command, EventMetadata metadata, CancellationToken cancellationToken)
    {
        var entryStream = StreamIds.Entry(command.EntryId);
        var entry = await streams.LoadAsync(entryStream, JournalEntryState.Initial(command.EntryId), (s, e) => s.Evolve(e), cancellationToken).ConfigureAwait(false);
        if (entry.State.ReversedBy == command.ReversalEntryId)
        {
            return CompareWithExisting(command, entry.Events);
        }

        if (entry.State.Posted is not { } posted)
        {
            return Result.Failure<EntryReceipt>(EntryErrors.NotFound(command.EntryId));
        }

        var periodId = PeriodId.FromBusinessDate(command.BusinessDate);
        var period = await streams.LoadAsync(StreamIds.Period(posted.Entity, periodId), PeriodState.Initial(posted.Entity, periodId), (s, e) => s.Evolve(e), cancellationToken).ConfigureAwait(false);
        var decision = JournalEntryDecider.Decide(entry.State, command, period.State);
        if (decision.IsFailure)
        {
            return Result.Failure<EntryReceipt>(decision.Error);
        }

        var batch = new PendingBatch(catalog, posted.Entity, command.BusinessDate, periodId.ToString(), metadata);
        var entryVersion = entry.Version;
        foreach (var domainEvent in decision.Value)
        {
            batch.Add(entryStream, ++entryVersion, domainEvent, claimedEntryId: command.ReversalEntryId.Value);
        }

        var reversal = decision.Value.OfType<JournalEntryReversed>().Single();
        var chart = await charts.LoadAsync(posted.Entity, cancellationToken).ConfigureAwait(false);
        if (await _constrainedLines.AddAsync(posted.Entity, command.ReversalEntryId, reversal.Lines, chart, batch, cancellationToken).ConfigureAwait(false) is { } accountRejection)
        {
            return Result.Failure<EntryReceipt>(accountRejection);
        }

        var committed = await batch.CommitAsync(appender, unitOfWork, cancellationToken).ConfigureAwait(false);
        return committed.Map(stored => EntryReceipt.From(command.ReversalEntryId, stored[0], alreadyExisted: false));
    }

    /// <summary>Même contre-passation (date et libellé) → l'existant ; sinon conflit sur <c>reversalEntryId</c>.</summary>
    private Result<EntryReceipt> CompareWithExisting(ReverseEntry command, IReadOnlyList<StoredEvent> events)
    {
        var stored = events.Single(e => e.EventType == EventTypes.JournalEntryReversed);
        var existing = (JournalEntryReversed)catalog.Deserialize(stored.EventType, stored.SchemaVersion, stored.Payload);
        return existing.BusinessDate == command.BusinessDate && existing.Description == command.Description
            ? Result.Success(EntryReceipt.From(command.ReversalEntryId, stored, alreadyExisted: true))
            : Result.Failure<EntryReceipt>(EntryApplicationErrors.EntryIdConflict(command.ReversalEntryId));
    }
}
