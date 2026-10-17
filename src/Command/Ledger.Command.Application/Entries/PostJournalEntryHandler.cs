using Ledger.Command.Application.Accounts;
using Ledger.Command.Application.Events;
using Ledger.Command.Application.EventStore;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Periods;
using Ledger.SharedKernel;

namespace Ledger.Command.Application.Entries;

/// <summary>
/// Accusé de passation : champs attribués par le serveur à l'événement qui crée l'écriture.
/// <see cref="AlreadyExisted"/> est vrai si la même écriture avait déjà été passée (rejeu idempotent).
/// </summary>
public sealed record EntryReceipt(EntryId EntryId, long GlobalSequence, DateTimeOffset RecordedAt, string Period, string Hash, bool AlreadyExisted)
{
    internal static EntryReceipt From(EntryId entryId, StoredEvent stored, bool alreadyExisted) =>
        new(entryId, stored.GlobalSequence, stored.RecordedAt, stored.Period, stored.Hash, alreadyExisted);
}

public static class EntryApplicationErrors
{
    /// <summary>L'identifiant d'écriture existe déjà avec une autre charge (ADR 0003).</summary>
    public const string EntryIdConflictCode = "EntryIdConflict";

    public static DomainError EntryIdConflict(EntryId entryId) =>
        DomainError.WithDetails(EntryIdConflictCode, $"L'écriture {entryId} existe déjà avec un contenu différent.", ("entryId", entryId.ToString()));
}

/// <summary>
/// Passe une écriture : <c>JournalEntryPosted</c> dans <c>entry-{id}</c> et un <c>AccountLinePosted</c>
/// par ligne de compte contraint, dans une seule transaction (ADR 0003 et 0004).
/// Les décisions sont prises sur le journal ; un conflit de version relance lecture et décision.
/// </summary>
public sealed class PostJournalEntryHandler(
    AggregateStreamReader streams,
    IEventAppender appender,
    IUnitOfWork unitOfWork,
    IChartOfAccountsReader charts,
    JournalEntryDecider decider,
    EventCatalog catalog,
    ConcurrencyRetryPolicy retryPolicy)
{
    private readonly ConstrainedAccountLines _constrainedLines = new(streams);

    public Task<Result<EntryReceipt>> HandleAsync(PostEntry command, EventMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(metadata);
        return retryPolicy.RunAsync(() => TryOnceAsync(command, metadata, cancellationToken));
    }

    private async Task<Result<EntryReceipt>> TryOnceAsync(PostEntry command, EventMetadata metadata, CancellationToken cancellationToken)
    {
        var entryStream = StreamIds.Entry(command.EntryId);
        var entry = await streams.LoadAsync(entryStream, JournalEntryState.Initial(command.EntryId), (s, e) => s.Evolve(e), cancellationToken).ConfigureAwait(false);
        if (entry.State.Posted is { } existing)
        {
            return CompareWithExisting(command, existing, entry.Events[0]);
        }

        var periodId = PeriodId.FromBusinessDate(command.BusinessDate);
        var period = await streams.LoadAsync(StreamIds.Period(command.Entity, periodId), PeriodState.Initial(command.Entity, periodId), (s, e) => s.Evolve(e), cancellationToken).ConfigureAwait(false);
        var chart = await charts.LoadAsync(command.Entity, cancellationToken).ConfigureAwait(false);

        var decision = decider.Decide(entry.State, command, period.State, chart);
        if (decision.IsFailure)
        {
            return Result.Failure<EntryReceipt>(decision.Error);
        }

        var batch = new PendingBatch(catalog, command.Entity, command.BusinessDate, periodId.ToString(), metadata);
        var entryVersion = entry.Version;
        foreach (var domainEvent in decision.Value)
        {
            batch.Add(entryStream, ++entryVersion, domainEvent, claimedEntryId: command.EntryId.Value);
        }

        if (await _constrainedLines.AddAsync(command.Entity, command.EntryId, command.Lines, chart, batch, cancellationToken).ConfigureAwait(false) is { } accountRejection)
        {
            return Result.Failure<EntryReceipt>(accountRejection);
        }

        var committed = await batch.CommitAsync(appender, unitOfWork, cancellationToken).ConfigureAwait(false);
        return committed.Map(stored => EntryReceipt.From(command.EntryId, stored[0], alreadyExisted: false));
    }

    /// <summary>Rejeu idempotent : même charge → l'existant ; charge différente → conflit (ADR 0003).</summary>
    private static Result<EntryReceipt> CompareWithExisting(PostEntry command, JournalEntryPosted existing, StoredEvent stored)
    {
        var candidate = new JournalEntryPosted(command.EntryId, command.Entity, command.BusinessDate, PeriodId.FromBusinessDate(command.BusinessDate), command.Description, command.Lines);
        return candidate == existing
            ? Result.Success(EntryReceipt.From(command.EntryId, stored, alreadyExisted: true))
            : Result.Failure<EntryReceipt>(EntryApplicationErrors.EntryIdConflict(command.EntryId));
    }
}
