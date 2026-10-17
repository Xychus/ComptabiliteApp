using Ledger.Command.Application.Entries;
using Ledger.Command.Application.Events;
using Ledger.Command.Application.EventStore;
using Ledger.Command.Application.Tests.Support;
using Ledger.Command.Domain.ConstrainedAccounts;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Periods;
using Ledger.Contracts.Events.V1;
using Ledger.SharedKernel;
using static Ledger.Command.Application.Tests.Support.InMemoryLedger;
using InMemoryLedger = Ledger.Command.Application.Tests.Support.InMemoryLedger;

namespace Ledger.Command.Application.Tests.Entries;

/// <summary>Contre-passation : journal → commande → écriture inverse et lignes de comptes contraints, ou rejet.</summary>
public sealed class ReverseJournalEntryHandlerTests
{
    private readonly InMemoryLedger _ledger = new();

    [Fact]
    public async Task La_contre_passation_s_ajoute_au_flux_de_l_ecriture_et_inverse_les_comptes_contraints()
    {
        await _ledger.OpenMarchAsync();
        var original = Entry(Debit(Cash, 100m), Credit(Sales, 100m));
        await _ledger.PostAsync(original);
        var command = new ReverseEntry(original.EntryId, EntryId.New(), March15, "Annulation");

        var result = await _ledger.ReverseAsync(command);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        var entry = await _ledger.ReadAsync(StreamIds.Entry(original.EntryId));
        Assert.Equal([EventTypes.JournalEntryPosted, EventTypes.JournalEntryReversed], entry.Select(e => e.EventType));
        Assert.Equal(new EntryReceipt(command.ReversalEntryId, entry[1].GlobalSequence, entry[1].RecordedAt, "2026-03", entry[1].Hash, AlreadyExisted: false), result.Value);
        var cash = await _ledger.ReadAsync(StreamIds.Account(Acme, Cash.Id));
        Assert.Equal([100m, 0m], cash.Select(e => e.BalanceAfter));
        Assert.Equal(
            new AccountLinePosted(Acme, Cash.Id, command.ReversalEntryId, Side.Credit, new Money(100m, Currency.Cad), new Money(0m, Currency.Cad)),
            EventCatalog.Default.Deserialize(cash[1].EventType, cash[1].SchemaVersion, cash[1].Payload));
    }

    [Fact]
    public async Task Rejouer_la_meme_contre_passation_renvoie_l_existante_sans_rien_ecrire()
    {
        await _ledger.OpenMarchAsync();
        var original = Entry(Debit(Receivables, 10m), Credit(Sales, 10m));
        await _ledger.PostAsync(original);
        var command = new ReverseEntry(original.EntryId, EntryId.New(), March15, null);
        var first = await _ledger.ReverseAsync(command);
        var outboxBefore = _ledger.Store.Outbox.Count;

        var replay = await _ledger.ReverseAsync(command);

        Assert.Equal(first.Value with { AlreadyExisted = true }, replay.Value);
        Assert.Equal(outboxBefore, _ledger.Store.Outbox.Count);
    }

    [Theory]
    [InlineData(16, null)]
    [InlineData(15, "Autre libellé")]
    public async Task Rejouer_avec_un_autre_contenu_est_un_conflit(int day, string? description)
    {
        await _ledger.OpenMarchAsync();
        var original = Entry(Debit(Receivables, 10m), Credit(Sales, 10m));
        await _ledger.PostAsync(original);
        var command = new ReverseEntry(original.EntryId, EntryId.New(), March15, null);
        await _ledger.ReverseAsync(command);

        var result = await _ledger.ReverseAsync(command with { BusinessDate = new DateOnly(2026, 3, day), Description = description });

        Assert.Equal(EntryApplicationErrors.EntryIdConflictCode, result.Error.Code);
    }

    [Fact]
    public async Task Une_ecriture_inconnue_ne_se_contre_passe_pas() =>
        Assert.Equal(EntryErrors.NotFoundCode, (await _ledger.ReverseAsync(new ReverseEntry(EntryId.New(), EntryId.New(), March15, null))).Error.Code);

    [Fact]
    public async Task Une_seconde_contre_passation_est_rejetee()
    {
        await _ledger.OpenMarchAsync();
        var original = Entry(Debit(Receivables, 10m), Credit(Sales, 10m));
        await _ledger.PostAsync(original);
        await _ledger.ReverseAsync(new ReverseEntry(original.EntryId, EntryId.New(), March15, null));

        var result = await _ledger.ReverseAsync(new ReverseEntry(original.EntryId, EntryId.New(), March15, null));

        Assert.Equal(EntryErrors.AlreadyReversedCode, result.Error.Code);
    }

    [Fact]
    public async Task La_periode_de_la_contre_passation_doit_etre_ouverte()
    {
        await _ledger.OpenMarchAsync();
        var original = Entry(Debit(Receivables, 10m), Credit(Sales, 10m));
        await _ledger.PostAsync(original);

        var result = await _ledger.ReverseAsync(new ReverseEntry(original.EntryId, EntryId.New(), new DateOnly(2026, 4, 1), null));

        Assert.Equal(PeriodErrors.NotOpenCode, result.Error.Code);
    }

    [Fact]
    public async Task Un_solde_contraint_insuffisant_rejette_la_contre_passation_et_rien_n_est_ecrit()
    {
        await _ledger.OpenMarchAsync();
        var deposit = Entry(Debit(Cash, 100m), Credit(Sales, 100m));
        await _ledger.PostAsync(deposit);
        await _ledger.PostAsync(Entry(Debit(Receivables, 60m), Credit(Cash, 60m)));

        var result = await _ledger.ReverseAsync(new ReverseEntry(deposit.EntryId, EntryId.New(), March15, null));

        Assert.Equal(ConstrainedAccountDecider.InsufficientBalanceCode, result.Error.Code);
        Assert.Single(await _ledger.ReadAsync(StreamIds.Entry(deposit.EntryId)));
    }

    [Fact]
    public async Task Un_conflit_de_version_relance_lecture_et_decision()
    {
        await _ledger.OpenMarchAsync();
        var deposit = Entry(Debit(Cash, 100m), Credit(Sales, 100m));
        await _ledger.PostAsync(deposit);
        var racing = new RacingUnitOfWork(_ledger.Store.BeginUnitOfWork(), async () => Assert.True((await _ledger.PostAsync(Entry(Debit(Cash, 5m), Credit(Sales, 5m)))).IsSuccess));

        var result = await _ledger.ReverseHandler(racing).HandleAsync(new ReverseEntry(deposit.EntryId, EntryId.New(), March15, null), Metadata, Ct);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(2, racing.Commits);
        Assert.Equal([100m, 105m, 5m], (await _ledger.ReadAsync(StreamIds.Account(Acme, Cash.Id))).Select(e => e.BalanceAfter));
    }

    [Fact]
    public async Task Les_tentatives_sont_bornees()
    {
        await _ledger.OpenMarchAsync();
        var original = Entry(Debit(Receivables, 10m), Credit(Sales, 10m));
        await _ledger.PostAsync(original);
        var conflicting = new AlwaysConflictingUnitOfWork();

        var result = await _ledger.ReverseHandler(conflicting, maxAttempts: 2).HandleAsync(new ReverseEntry(original.EntryId, EntryId.New(), March15, null), Metadata, Ct);

        Assert.Equal(EventStoreErrors.ConcurrencyConflictCode, result.Error.Code);
        Assert.Equal(2, conflicting.Commits);
    }
}
