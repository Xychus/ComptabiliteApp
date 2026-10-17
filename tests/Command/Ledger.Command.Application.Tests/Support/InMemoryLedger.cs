using Ledger.Command.Application.Entries;
using Ledger.Command.Application.Events;
using Ledger.Command.Application.EventStore;
using Ledger.Command.Domain;
using Ledger.Command.Domain.Accounts;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Periods;
using Ledger.Command.Infrastructure.InMemory;
using Ledger.SharedKernel;

namespace Ledger.Command.Application.Tests.Support;

/// <summary>Grand livre en mémoire : event store, plan comptable et fabriques de commandes.</summary>
internal sealed class InMemoryLedger
{
    public static readonly EntityId Acme = new("ACME");
    public static readonly AccountInfo Cash = new(new AccountId("1000"), Currency.Cad, Side.Debit, IsActive: true, NoNegativeBalance: true);
    public static readonly AccountInfo Receivables = new(new AccountId("1100"), Currency.Cad, Side.Debit, IsActive: true, NoNegativeBalance: false);
    public static readonly AccountInfo Sales = new(new AccountId("4000"), Currency.Cad, Side.Credit, IsActive: true, NoNegativeBalance: false);
    public static readonly DateOnly March15 = new(2026, 3, 15);
    public static readonly PeriodId March = new(2026, 3);
    public static readonly EventMetadata Metadata = new(Guid.CreateVersion7(), null, "caisse-1");

    public InMemoryLedger()
    {
        Store = new InMemoryEventStore(new SystemClock());
        foreach (var account in new[] { Cash, Receivables, Sales })
        {
            Chart.Define(Acme, account);
        }
    }

    public InMemoryEventStore Store { get; }

    public InMemoryChartOfAccounts Chart { get; } = new();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PostJournalEntryHandler Handler() => Handler(Store.BeginUnitOfWork());

    public PostJournalEntryHandler Handler<TUnitOfWork>(TUnitOfWork unitOfWork, int maxAttempts = 5)
        where TUnitOfWork : IEventAppender, IUnitOfWork =>
        new(
            new AggregateStreamReader(Store, EventCatalog.Default),
            unitOfWork,
            unitOfWork,
            Chart,
            JournalEntryDecider.Default,
            EventCatalog.Default,
            new ConcurrencyRetryPolicy(maxAttempts));

    public Task<Result<EntryReceipt>> PostAsync(PostEntry command) => Handler().HandleAsync(command, Metadata, Ct);

    /// <summary>Ajoute un événement de domaine au journal, comme le ferait une autre commande.</summary>
    public async Task AppendAsync(string streamId, long version, IDomainEvent domainEvent, DateOnly? businessDate = null, string period = "2026-03", Guid? claimedEntryId = null)
    {
        var serialized = EventCatalog.Default.Serialize(domainEvent);
        var unitOfWork = Store.BeginUnitOfWork();
        unitOfWork.Append(new EventToAppend(
            Guid.CreateVersion7(), streamId, version, serialized.EventType, serialized.SchemaVersion, Acme.Value, businessDate, period, serialized.Payload, null, Metadata, claimedEntryId));
        var result = await unitOfWork.CommitAsync(Ct);
        Assert.True(result.IsSuccess);
    }

    public Task OpenMarchAsync() => AppendAsync(StreamIds.Period(Acme, March), 1, new PeriodOpened(Acme, March));

    public Task SealMarchAsync() => AppendAsync(StreamIds.Period(Acme, March), 2, new PeriodSealed(Acme, March));

    public Task<IReadOnlyList<StoredEvent>> ReadAsync(string streamId) => Store.ReadStreamAsync(streamId, Ct);

    public static PostEntry Entry(params JournalLine[] lines) => new(EntryId.New(), Acme, March15, "Test", EquatableList.Of(lines));

    public static JournalLine Debit(AccountInfo account, decimal amount) => new(account.Id, Side.Debit, new Money(amount, account.Currency));

    public static JournalLine Credit(AccountInfo account, decimal amount) => new(account.Id, Side.Credit, new Money(amount, account.Currency));
}
