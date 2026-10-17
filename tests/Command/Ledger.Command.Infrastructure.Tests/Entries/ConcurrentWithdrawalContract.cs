using Ledger.Command.Application.Accounts;
using Ledger.Command.Application.Entries;
using Ledger.Command.Application.Events;
using Ledger.Command.Application.EventStore;
using Ledger.Command.Domain.Accounts;
using Ledger.Command.Domain.ConstrainedAccounts;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Periods;
using Ledger.Command.Infrastructure.InMemory;
using Ledger.Command.Infrastructure.Postgres;
using Ledger.Command.Infrastructure.Tests.EventStore;
using Ledger.Command.Infrastructure.Tests.Postgres;
using Ledger.SharedKernel;

namespace Ledger.Command.Infrastructure.Tests.Entries;

/// <summary>
/// Critère d'acceptation de la phase 2 : deux retraits simultanés de 60 $ sur un compte contraint
/// à 100 $ → exactement un accepté, solde final 40 $ ; 100 fois de suite (ADR 0004).
/// </summary>
public abstract class ConcurrentWithdrawalContract
{
    private const int Runs = 100;
    private static readonly AccountInfo _cash = new(new AccountId("1000"), Currency.Cad, Side.Debit, IsActive: true, NoNegativeBalance: true);
    private static readonly AccountInfo _expenses = new(new AccountId("5000"), Currency.Cad, Side.Debit, IsActive: true, NoNegativeBalance: false);
    private static readonly AccountInfo _capital = new(new AccountId("3000"), Currency.Cad, Side.Credit, IsActive: true, NoNegativeBalance: false);
    private static readonly DateOnly _date = new(2026, 3, 15);
    private static readonly PeriodId _march = new(2026, 3);
    private static readonly EventMetadata _metadata = new(Guid.CreateVersion7(), null, "tests-concurrence");

    protected abstract IEventReader Reader { get; }

    protected abstract IChartOfAccountsReader Chart { get; }

    protected abstract UnitOfWorkUnderTest BeginUnitOfWork();

    protected abstract Task DefineAccountAsync(EntityId entity, AccountInfo account);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Deux_retraits_simultanes_de_60_sur_100_un_seul_accepte_cent_fois_de_suite()
    {
        var commits = 0;
        for (var run = 0; run < Runs; run++)
        {
            var entity = new EntityId("CONC-" + Guid.CreateVersion7().ToString("N"));
            await PrepareAccountWith100Async(entity);
            using var start = new Barrier(2);

            var results = await Task.WhenAll(
                Task.Run(() => WithdrawAsync(entity, start), Ct),
                Task.Run(() => WithdrawAsync(entity, start), Ct));

            Assert.Single(results, r => r.Result.IsSuccess);
            Assert.Equal(ConstrainedAccountDecider.InsufficientBalanceCode, Assert.Single(results, r => r.Result.IsFailure).Result.Error.Code);
            var account = await Reader.ReadStreamAsync(StreamIds.Account(entity, _cash.Id), Ct);
            Assert.Equal([100m, 40m], account.Select(e => e.BalanceAfter));
            commits += results.Sum(r => r.Commits);
        }

        // Une validation gagnante par essai ; toute autre validation a été rejetée pour conflit de version.
        TestContext.Current.TestOutputHelper?.WriteLine($"{Runs} essais, {commits} validations, dont {commits - Runs} rejetées pour conflit de version.");
    }

    private async Task PrepareAccountWith100Async(EntityId entity)
    {
        foreach (var account in new[] { _cash, _expenses, _capital })
        {
            await DefineAccountAsync(entity, account);
        }

        var opened = EventCatalog.Default.Serialize(new PeriodOpened(entity, _march));
        var result = await BeginUnitOfWork().CommitAsync(new EventToAppend(
            Guid.CreateVersion7(), StreamIds.Period(entity, _march), 1, opened.EventType, opened.SchemaVersion, entity.Value, null, _march.ToString(), opened.Payload, null, _metadata));
        Assert.True(result.IsSuccess);

        var deposit = await Handler(BeginUnitOfWork()).HandleAsync(Entry(entity, Line(_cash, Side.Debit, 100m), Line(_capital, Side.Credit, 100m)), _metadata, Ct);
        Assert.True(deposit.IsSuccess);
    }

    private async Task<(Result<EntryReceipt> Result, int Commits)> WithdrawAsync(EntityId entity, Barrier start)
    {
        var unitOfWork = new CountingUnitOfWork(BeginUnitOfWork());
        var command = Entry(entity, Line(_expenses, Side.Debit, 60m), Line(_cash, Side.Credit, 60m));
        start.SignalAndWait(Ct);
        var result = await Handler(unitOfWork).HandleAsync(command, _metadata, Ct);
        return (result, unitOfWork.Commits);
    }

    private PostJournalEntryHandler Handler<TUnitOfWork>(TUnitOfWork unitOfWork)
        where TUnitOfWork : IEventAppender, IUnitOfWork =>
        new(new AggregateStreamReader(Reader, EventCatalog.Default), unitOfWork, unitOfWork, Chart, JournalEntryDecider.Default, EventCatalog.Default, ConcurrencyRetryPolicy.Default);

    private PostJournalEntryHandler Handler(UnitOfWorkUnderTest unitOfWork) => Handler(new CountingUnitOfWork(unitOfWork));

    private static PostEntry Entry(EntityId entity, params JournalLine[] lines) => new(EntryId.New(), entity, _date, null, EquatableList.Of(lines));

    private static JournalLine Line(AccountInfo account, Side side, decimal amount) => new(account.Id, side, new Money(amount, account.Currency));

    private sealed class CountingUnitOfWork(UnitOfWorkUnderTest inner) : IEventAppender, IUnitOfWork
    {
        public int Commits { get; private set; }

        public void Append(EventToAppend eventToAppend) => inner.Appender.Append(eventToAppend);

        public Task<Result<IReadOnlyList<StoredEvent>>> CommitAsync(CancellationToken cancellationToken)
        {
            Commits++;
            return inner.UnitOfWork.CommitAsync(cancellationToken);
        }
    }
}

public sealed class InMemoryConcurrentWithdrawalTests : ConcurrentWithdrawalContract
{
    private readonly InMemoryEventStore _store = new(new SystemClock());
    private readonly InMemoryChartOfAccounts _chart = new();

    protected override IEventReader Reader => _store;

    protected override IChartOfAccountsReader Chart => _chart;

    protected override UnitOfWorkUnderTest BeginUnitOfWork()
    {
        var unitOfWork = _store.BeginUnitOfWork();
        return new UnitOfWorkUnderTest(unitOfWork, unitOfWork);
    }

    protected override Task DefineAccountAsync(EntityId entity, AccountInfo account)
    {
        _chart.Define(entity, account);
        return Task.CompletedTask;
    }
}

[Collection(PostgresGroup.Name)]
[Trait("Category", "Integration")]
public sealed class NpgsqlConcurrentWithdrawalTests(PostgresFixture database) : ConcurrentWithdrawalContract
{
    protected override IEventReader Reader { get; } = new NpgsqlEventStore(database.AppDataSource);

    protected override IChartOfAccountsReader Chart { get; } = new NpgsqlChartOfAccountsReader(database.AppDataSource);

    protected override UnitOfWorkUnderTest BeginUnitOfWork()
    {
        var unitOfWork = new NpgsqlUnitOfWork(database.AppDataSource, new SystemClock());
        return new UnitOfWorkUnderTest(unitOfWork, unitOfWork);
    }

    protected override Task DefineAccountAsync(EntityId entity, AccountInfo account) => database.DefineAccountAsync(entity, account);
}
