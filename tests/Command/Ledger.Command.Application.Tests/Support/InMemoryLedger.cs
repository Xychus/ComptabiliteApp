using Ledger.Command.Application.Accounts;
using Ledger.Command.Application.Entries;
using Ledger.Command.Application.Events;
using Ledger.Command.Application.EventStore;
using Ledger.Command.Application.Periods;
using Ledger.Command.Domain;
using Ledger.Command.Domain.Accounts;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Periods;
using Ledger.Command.Infrastructure.InMemory;
using Ledger.Contracts.Integrity;
using Ledger.SharedKernel;

namespace Ledger.Command.Application.Tests.Support;

/// <summary>Grand livre en mémoire : event store, plan comptable (flux <c>chart-ACME</c>) et fabriques de commandes.</summary>
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
        Streams = new AggregateStreamReader(Store, EventCatalog.Default);
        Chart = new JournalChartOfAccountsReader(Streams);
        var unitOfWork = Store.BeginUnitOfWork();
        var version = 0L;
        foreach (var account in new[] { Cash, Receivables, Sales })
        {
            var defined = EventCatalog.Default.Serialize(new AccountDefined(Acme, account.Id, "Compte " + account.Id, account.Currency, account.NormalSide, account.IsActive, account.NoNegativeBalance));
            unitOfWork.Append(new EventToAppend(Guid.CreateVersion7(), StreamIds.Chart(Acme), ++version, defined.EventType, defined.SchemaVersion, Acme.Value, null, "2026-01", defined.Payload, null, Metadata));
        }

        // L'event store en mémoire valide de façon synchrone.
        Assert.True(unitOfWork.CommitAsync(CancellationToken.None).GetAwaiter().GetResult().IsSuccess);
    }

    public InMemoryEventStore Store { get; }

    public AggregateStreamReader Streams { get; }

    public IChartOfAccountsReader Chart { get; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public PostJournalEntryHandler Handler() => Handler(Store.BeginUnitOfWork());

    public PostJournalEntryHandler Handler<TUnitOfWork>(TUnitOfWork unitOfWork, int maxAttempts = 5)
        where TUnitOfWork : IEventAppender, IUnitOfWork =>
        new(
            Streams,
            unitOfWork,
            unitOfWork,
            Chart,
            JournalEntryDecider.Default,
            EventCatalog.Default,
            new ConcurrencyRetryPolicy(maxAttempts));

    public Task<Result<EntryReceipt>> PostAsync(PostEntry command) => Handler().HandleAsync(command, Metadata, Ct);

    public ReverseJournalEntryHandler ReverseHandler<TUnitOfWork>(TUnitOfWork unitOfWork, int maxAttempts = 5)
        where TUnitOfWork : IEventAppender, IUnitOfWork =>
        new(Streams, unitOfWork, unitOfWork, Chart, EventCatalog.Default, new ConcurrencyRetryPolicy(maxAttempts));

    public Task<Result<EntryReceipt>> ReverseAsync(ReverseEntry command) => ReverseHandler(Store.BeginUnitOfWork()).HandleAsync(command, Metadata, Ct);

    public PeriodJournal Periods<TUnitOfWork>(TUnitOfWork unitOfWork)
        where TUnitOfWork : IEventAppender, IUnitOfWork =>
        new(Streams, unitOfWork, unitOfWork, EventCatalog.Default);

    public SealPeriodHandler SealHandler<TUnitOfWork>(TUnitOfWork unitOfWork, IPeriodEventReader? periodEvents = null, int maxAttempts = 5)
        where TUnitOfWork : IEventAppender, IUnitOfWork =>
        new(Periods(unitOfWork), periodEvents ?? Store, new ConcurrencyRetryPolicy(maxAttempts));

    public Task<Result<PeriodReceipt>> OpenAsync(PeriodId period, long expectedVersion) =>
        new OpenPeriodHandler(Periods(Store.BeginUnitOfWork())).HandleAsync(new PeriodCommand(Acme, period, expectedVersion), Metadata, Ct);

    public Task<Result<PeriodReceipt>> SealAsync(PeriodId period, long expectedVersion) =>
        SealHandler(Store.BeginUnitOfWork()).HandleAsync(new PeriodCommand(Acme, period, expectedVersion), Metadata, Ct);

    public DefineAccountHandler DefineHandler<TUnitOfWork>(TUnitOfWork unitOfWork, IClock? clock = null, int maxAttempts = 5)
        where TUnitOfWork : IEventAppender, IUnitOfWork =>
        new(Streams, unitOfWork, unitOfWork, EventCatalog.Default, clock ?? new SystemClock(), new ConcurrencyRetryPolicy(maxAttempts));

    public Task<Result<AccountReceipt>> DefineAsync(DefineAccount command) => DefineHandler(Store.BeginUnitOfWork()).HandleAsync(command, Metadata, Ct);

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

    /// <summary>Scelle mars en décomptant ses événements, comme le ferait la commande de scellement.</summary>
    public async Task SealMarchAsync()
    {
        var hashes = await Store.ReadPeriodHashesAsync(Acme.Value, March.ToString(), Ct);
        await AppendAsync(StreamIds.Period(Acme, March), 2, new PeriodSealed(Acme, March, new PeriodDigest(hashes.Count, MerkleTree.Root(hashes))));
    }

    public Task<IReadOnlyList<StoredEvent>> ReadAsync(string streamId) => Store.ReadStreamAsync(streamId, Ct);

    public static PostEntry Entry(params JournalLine[] lines) => new(EntryId.New(), Acme, March15, "Test", EquatableList.Of(lines));

    public static JournalLine Debit(AccountInfo account, decimal amount) => new(account.Id, Side.Debit, new Money(amount, account.Currency));

    public static JournalLine Credit(AccountInfo account, decimal amount) => new(account.Id, Side.Credit, new Money(amount, account.Currency));
}
