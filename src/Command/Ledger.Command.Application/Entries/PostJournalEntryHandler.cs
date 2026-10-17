using Ledger.Command.Application.Accounts;
using Ledger.Command.Application.Events;
using Ledger.Command.Application.EventStore;
using Ledger.Command.Domain;
using Ledger.Command.Domain.Accounts;
using Ledger.Command.Domain.ConstrainedAccounts;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Periods;
using Ledger.SharedKernel;

namespace Ledger.Command.Application.Entries;

/// <summary>
/// Accusé de passation : champs attribués par le serveur à <c>JournalEntryPosted</c>.
/// <see cref="AlreadyExisted"/> est vrai si la même écriture avait déjà été passée (rejeu idempotent).
/// </summary>
public sealed record EntryReceipt(EntryId EntryId, long GlobalSequence, DateTimeOffset RecordedAt, string Period, string Hash, bool AlreadyExisted);

/// <summary>Nombre maximal de tentatives sur conflit de concurrence (ADR 0003).</summary>
public sealed record ConcurrencyRetryPolicy(int MaxAttempts)
{
    public static ConcurrencyRetryPolicy Default { get; } = new(5);
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
    public async Task<Result<EntryReceipt>> HandleAsync(PostEntry command, EventMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(metadata);
        for (var attempt = 1; ; attempt++)
        {
            var outcome = await TryOnceAsync(command, metadata, cancellationToken).ConfigureAwait(false);
            if (outcome.IsSuccess || outcome.Error.Code != EventStoreErrors.ConcurrencyConflictCode || attempt >= retryPolicy.MaxAttempts)
            {
                return outcome;
            }
        }
    }

    private async Task<Result<EntryReceipt>> TryOnceAsync(PostEntry command, EventMetadata metadata, CancellationToken cancellationToken)
    {
        var entry = await streams.LoadAsync(StreamIds.Entry(command.EntryId), JournalEntryState.Initial(command.EntryId), (s, e) => s.Evolve(e), cancellationToken).ConfigureAwait(false);
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

        var batch = new PendingBatch(catalog, command.Entity, command.BusinessDate, periodId, metadata);
        var entryVersion = entry.Version;
        foreach (var domainEvent in decision.Value)
        {
            batch.Add(StreamIds.Entry(command.EntryId), ++entryVersion, domainEvent, balanceAfter: null, claimedEntryId: command.EntryId.Value);
        }

        if (await AddConstrainedAccountLinesAsync(command, chart, batch, cancellationToken).ConfigureAwait(false) is { } accountRejection)
        {
            return Result.Failure<EntryReceipt>(accountRejection);
        }

        foreach (var pending in batch.Events)
        {
            appender.Append(pending);
        }

        var committed = await unitOfWork.CommitAsync(cancellationToken).ConfigureAwait(false);
        return committed.Map(stored => Receipt(command.EntryId, stored[0], alreadyExisted: false));
    }

    /// <summary>Applique chaque ligne de compte contraint à la version suivante du flux du compte (ADR 0004).</summary>
    private async Task<DomainError?> AddConstrainedAccountLinesAsync(PostEntry command, IChartOfAccounts chart, PendingBatch batch, CancellationToken cancellationToken)
    {
        var accounts = new Dictionary<AccountId, (ConstrainedAccountState State, long Version)>();
        foreach (var line in command.Lines)
        {
            if (chart.Find(line.Account) is not { NoNegativeBalance: true } account)
            {
                continue;
            }

            var streamId = StreamIds.Account(command.Entity, account.Id);
            if (!accounts.TryGetValue(account.Id, out var current))
            {
                var loaded = await streams.LoadAsync(streamId, ConstrainedAccountState.Initial(command.Entity, account), (s, e) => s.Evolve(e), cancellationToken).ConfigureAwait(false);
                current = (loaded.State, loaded.Version);
            }

            var decision = ConstrainedAccountDecider.Decide(current.State, new ApplyAccountLine(command.EntryId, line));
            if (decision.IsFailure)
            {
                return decision.Error;
            }

            foreach (var domainEvent in decision.Value)
            {
                batch.Add(streamId, ++current.Version, domainEvent, ((AccountLinePosted)domainEvent).BalanceAfter.Amount, claimedEntryId: null);
                current.State = current.State.Evolve(domainEvent);
            }

            accounts[account.Id] = current;
        }

        return null;
    }

    /// <summary>Rejeu idempotent : même charge → l'existant ; charge différente → conflit (ADR 0003).</summary>
    private static Result<EntryReceipt> CompareWithExisting(PostEntry command, JournalEntryPosted existing, StoredEvent stored)
    {
        var candidate = new JournalEntryPosted(command.EntryId, command.Entity, command.BusinessDate, PeriodId.FromBusinessDate(command.BusinessDate), command.Description, command.Lines);
        return candidate == existing
            ? Result.Success(Receipt(command.EntryId, stored, alreadyExisted: true))
            : Result.Failure<EntryReceipt>(EntryApplicationErrors.EntryIdConflict(command.EntryId));
    }

    private static EntryReceipt Receipt(EntryId entryId, StoredEvent posted, bool alreadyExisted) =>
        new(entryId, posted.GlobalSequence, posted.RecordedAt, posted.Period, posted.Hash, alreadyExisted);

    /// <summary>Événements décidés, ajoutés à l'unité de travail seulement si toutes les décisions sont acceptées.</summary>
    private sealed class PendingBatch(EventCatalog catalog, EntityId entity, DateOnly businessDate, PeriodId period, EventMetadata metadata)
    {
        private readonly List<EventToAppend> _events = [];

        public IReadOnlyList<EventToAppend> Events => _events;

        public void Add(string streamId, long version, IDomainEvent domainEvent, decimal? balanceAfter, Guid? claimedEntryId)
        {
            var serialized = catalog.Serialize(domainEvent);
            _events.Add(new EventToAppend(
                Guid.CreateVersion7(),
                streamId,
                version,
                serialized.EventType,
                serialized.SchemaVersion,
                entity.Value,
                businessDate,
                period.ToString(),
                serialized.Payload,
                balanceAfter,
                metadata,
                claimedEntryId));
        }
    }
}
