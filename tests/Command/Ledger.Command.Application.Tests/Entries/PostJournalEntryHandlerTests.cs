using Ledger.Command.Application.Entries;
using Ledger.Command.Application.Events;
using Ledger.Command.Application.EventStore;
using Ledger.Command.Domain.ConstrainedAccounts;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Periods;
using Ledger.Command.Infrastructure.InMemory;
using Ledger.Contracts.Events.V1;
using Ledger.SharedKernel;
using static Ledger.Command.Application.Tests.Support.InMemoryLedger;
using InMemoryLedger = Ledger.Command.Application.Tests.Support.InMemoryLedger;

namespace Ledger.Command.Application.Tests.Entries;

/// <summary>Passation d'une écriture : journal → commande → événements écrits ou rejet (ADR 0003 et 0004).</summary>
public sealed class PostJournalEntryHandlerTests
{
    private readonly InMemoryLedger _ledger = new();

    [Fact]
    public async Task Une_ecriture_sans_compte_contraint_ecrit_un_seul_evenement()
    {
        await _ledger.OpenMarchAsync();
        var command = Entry(Debit(Receivables, 100m), Credit(Sales, 100m));

        var result = await _ledger.PostAsync(command);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        var stored = Assert.Single(await _ledger.ReadAsync(StreamIds.Entry(command.EntryId)));
        Assert.Equal(EventTypes.JournalEntryPosted, stored.EventType);
        Assert.Equal(new EntryReceipt(command.EntryId, stored.GlobalSequence, stored.RecordedAt, "2026-03", stored.Hash, AlreadyExisted: false), result.Value);
        Assert.Equal(Metadata, stored.Metadata);
        Assert.Equal(March15, stored.BusinessDate);
        Assert.Null(stored.BalanceAfter);
        Assert.Empty(await _ledger.ReadAsync(StreamIds.Account(Acme, Receivables.Id)));
    }

    [Fact]
    public async Task Une_ligne_de_compte_contraint_avance_le_flux_du_compte_avec_balance_after()
    {
        await _ledger.OpenMarchAsync();
        var deposit = Entry(Debit(Cash, 100m), Credit(Sales, 100m));
        var withdrawal = Entry(Debit(Receivables, 60m), Credit(Cash, 60m));

        await _ledger.PostAsync(deposit);
        var result = await _ledger.PostAsync(withdrawal);

        Assert.True(result.IsSuccess);
        var account = await _ledger.ReadAsync(StreamIds.Account(Acme, Cash.Id));
        Assert.Equal([1L, 2L], account.Select(e => e.StreamVersion));
        Assert.Equal([100m, 40m], account.Select(e => e.BalanceAfter));
        Assert.All(account, e => Assert.Equal(EventTypes.AccountLinePosted, e.EventType));
        Assert.All(account, e => Assert.Equal(March15, e.BusinessDate));
        Assert.Equal(
            new AccountLinePosted(Acme, Cash.Id, withdrawal.EntryId, Side.Credit, new Money(60m, Currency.Cad), new Money(40m, Currency.Cad)),
            EventCatalog.Default.Deserialize(account[1].EventType, account[1].SchemaVersion, account[1].Payload));
    }

    [Fact]
    public async Task Plusieurs_lignes_sur_le_meme_compte_contraint_prennent_des_versions_consecutives()
    {
        await _ledger.OpenMarchAsync();

        var result = await _ledger.PostAsync(Entry(Debit(Cash, 70m), Debit(Cash, 30m), Credit(Sales, 100m)));

        Assert.True(result.IsSuccess);
        var account = await _ledger.ReadAsync(StreamIds.Account(Acme, Cash.Id));
        Assert.Equal([70m, 100m], account.Select(e => e.BalanceAfter));
        Assert.Equal(account[0].Hash, account[1].PreviousHash);
    }

    [Fact]
    public async Task Un_solde_insuffisant_est_rejete_et_rien_n_est_ecrit()
    {
        await _ledger.OpenMarchAsync();
        await _ledger.PostAsync(Entry(Debit(Cash, 100m), Credit(Sales, 100m)));
        var command = Entry(Debit(Receivables, 100.01m), Credit(Cash, 100.01m));

        var result = await _ledger.PostAsync(command);

        Assert.Equal(ConstrainedAccountDecider.InsufficientBalanceCode, result.Error.Code);
        Assert.Empty(await _ledger.ReadAsync(StreamIds.Entry(command.EntryId)));
        Assert.Single(await _ledger.ReadAsync(StreamIds.Account(Acme, Cash.Id)));
    }

    [Fact]
    public async Task Une_regle_d_ecriture_violee_est_rejetee_et_rien_n_est_ecrit()
    {
        await _ledger.OpenMarchAsync();
        var command = Entry(Debit(Cash, 100m), Credit(Sales, 99m));

        var result = await _ledger.PostAsync(command);

        Assert.Equal(EntryErrors.NotBalancedCode, result.Error.Code);
        Assert.Empty(await _ledger.ReadAsync(StreamIds.Entry(command.EntryId)));
        Assert.Empty(await _ledger.ReadAsync(StreamIds.Account(Acme, Cash.Id)));
    }

    [Fact]
    public async Task Rejouer_la_meme_ecriture_renvoie_l_existante_sans_rien_ecrire()
    {
        await _ledger.OpenMarchAsync();
        var command = Entry(Debit(Cash, 100m), Credit(Sales, 100m));
        var first = await _ledger.PostAsync(command);
        var outboxBefore = _ledger.Store.Outbox.Count;

        var replay = await _ledger.PostAsync(command with { Lines = EquatableList.Of(Debit(Cash, 100.00m), Credit(Sales, 100.00m)) });

        Assert.True(replay.IsSuccess);
        Assert.Equal(first.Value with { AlreadyExisted = true }, replay.Value);
        Assert.Equal(outboxBefore, _ledger.Store.Outbox.Count);
        Assert.Single(await _ledger.ReadAsync(StreamIds.Account(Acme, Cash.Id)));
    }

    [Fact]
    public async Task Le_meme_identifiant_avec_un_autre_contenu_est_un_conflit()
    {
        await _ledger.OpenMarchAsync();
        var command = Entry(Debit(Cash, 100m), Credit(Sales, 100m));
        await _ledger.PostAsync(command);

        var result = await _ledger.PostAsync(command with { Description = "Autre" });

        Assert.Equal(EntryApplicationErrors.EntryIdConflictCode, result.Error.Code);
    }

    [Fact]
    public async Task Un_identifiant_reserve_par_une_contre_passation_est_refuse()
    {
        await _ledger.OpenMarchAsync();
        var original = Entry(Debit(Receivables, 100m), Credit(Sales, 100m));
        await _ledger.PostAsync(original);
        var command = Entry(Debit(Cash, 100m), Credit(Sales, 100m));
        var reversal = new JournalEntryReversed(command.EntryId, original.EntryId, Acme, March15, March, null, EquatableList.Of(original.Lines.Select(l => l.Inverted())));
        await _ledger.AppendAsync(StreamIds.Entry(original.EntryId), 2, reversal, March15, claimedEntryId: command.EntryId.Value);
        var counting = new CountingUnitOfWork(_ledger.Store.BeginUnitOfWork());

        var result = await _ledger.Handler(counting).HandleAsync(command, Metadata, Ct);

        Assert.Equal(EventStoreErrors.EntryIdAlreadyUsedCode, result.Error.Code);
        Assert.Equal(1, counting.Commits);
    }

    [Fact]
    public async Task Une_periode_non_ouverte_est_rejetee() =>
        Assert.Equal(PeriodErrors.NotOpenCode, (await _ledger.PostAsync(Entry(Debit(Cash, 1m), Credit(Sales, 1m)))).Error.Code);

    [Fact]
    public async Task Une_periode_scellee_est_rejetee()
    {
        await _ledger.OpenMarchAsync();
        await _ledger.SealMarchAsync();

        Assert.Equal(PeriodErrors.SealedCode, (await _ledger.PostAsync(Entry(Debit(Cash, 1m), Credit(Sales, 1m)))).Error.Code);
    }

    [Fact]
    public async Task Un_conflit_de_version_relance_lecture_et_decision()
    {
        await _ledger.OpenMarchAsync();
        await _ledger.PostAsync(Entry(Debit(Cash, 100m), Credit(Sales, 100m)));
        var competitor = Entry(Debit(Receivables, 30m), Credit(Cash, 30m));
        var racing = new RacingUnitOfWork(_ledger.Store.BeginUnitOfWork(), () => _ledger.PostAsync(competitor));

        var result = await _ledger.Handler(racing).HandleAsync(Entry(Debit(Receivables, 60m), Credit(Cash, 60m)), Metadata, Ct);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(2, racing.Commits);
        Assert.Equal([100m, 70m, 10m], (await _ledger.ReadAsync(StreamIds.Account(Acme, Cash.Id))).Select(e => e.BalanceAfter));
    }

    [Fact]
    public async Task Apres_un_conflit_la_nouvelle_decision_peut_rejeter()
    {
        await _ledger.OpenMarchAsync();
        await _ledger.PostAsync(Entry(Debit(Cash, 100m), Credit(Sales, 100m)));
        var racing = new RacingUnitOfWork(_ledger.Store.BeginUnitOfWork(), () => _ledger.PostAsync(Entry(Debit(Receivables, 60m), Credit(Cash, 60m))));

        var result = await _ledger.Handler(racing).HandleAsync(Entry(Debit(Receivables, 60m), Credit(Cash, 60m)), Metadata, Ct);

        Assert.Equal(ConstrainedAccountDecider.InsufficientBalanceCode, result.Error.Code);
        Assert.Equal([100m, 40m], (await _ledger.ReadAsync(StreamIds.Account(Acme, Cash.Id))).Select(e => e.BalanceAfter));
    }

    [Fact]
    public async Task Les_tentatives_sont_bornees()
    {
        await _ledger.OpenMarchAsync();
        var conflicting = new AlwaysConflictingUnitOfWork();

        var result = await _ledger.Handler(conflicting, maxAttempts: 3).HandleAsync(Entry(Debit(Cash, 1m), Credit(Sales, 1m)), Metadata, Ct);

        Assert.Equal(EventStoreErrors.ConcurrencyConflictCode, result.Error.Code);
        Assert.Equal(3, conflicting.Commits);
    }

    /// <summary>Compte les validations.</summary>
    private sealed class CountingUnitOfWork(InMemoryUnitOfWork inner) : IEventAppender, IUnitOfWork
    {
        public int Commits { get; private set; }

        public void Append(EventToAppend eventToAppend) => inner.Append(eventToAppend);

        public Task<Result<IReadOnlyList<StoredEvent>>> CommitAsync(CancellationToken cancellationToken)
        {
            Commits++;
            return inner.CommitAsync(cancellationToken);
        }
    }

    /// <summary>Fait valider une écriture concurrente juste avant la première validation.</summary>
    private sealed class RacingUnitOfWork(InMemoryUnitOfWork inner, Func<Task<Result<EntryReceipt>>> competitor) : IEventAppender, IUnitOfWork
    {
        public int Commits { get; private set; }

        public void Append(EventToAppend eventToAppend) => inner.Append(eventToAppend);

        public async Task<Result<IReadOnlyList<StoredEvent>>> CommitAsync(CancellationToken cancellationToken)
        {
            if (++Commits == 1)
            {
                Assert.True((await competitor()).IsSuccess);
            }

            return await inner.CommitAsync(cancellationToken);
        }
    }

    private sealed class AlwaysConflictingUnitOfWork : IEventAppender, IUnitOfWork
    {
        public int Commits { get; private set; }

        public void Append(EventToAppend eventToAppend)
        {
        }

        public Task<Result<IReadOnlyList<StoredEvent>>> CommitAsync(CancellationToken cancellationToken)
        {
            Commits++;
            return Task.FromResult(Result.Failure<IReadOnlyList<StoredEvent>>(EventStoreErrors.ConcurrencyConflict()));
        }
    }
}
