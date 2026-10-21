using Ledger.Projections.Application.Chart;
using Ledger.Projections.Application.Entries;
using Ledger.Projections.Application.Events;
using Ledger.Projections.Application.Periods;
using Ledger.Projections.Application.Tests.Support;
using Ledger.Projections.Infrastructure;
using Ledger.Projections.Infrastructure.InMemory;
using Ledger.Projections.Tests.Support;
using static Ledger.Projections.Tests.Support.JournalBuilder;

namespace Ledger.Projections.Application.Tests.Projections;

/// <summary>Chaque projection : événements livrés → lignes attendues de ses tables.</summary>
public sealed class ProjectionBehaviourTests : IAsyncDisposable
{
    private static readonly DateOnly _march2 = new(2026, 3, 2);
    private static readonly DateOnly _march9 = new(2026, 3, 9);
    private static readonly DateOnly _april3 = new(2026, 4, 3);

    private readonly InMemoryProjections _projections = new();
    private readonly JournalBuilder _acme = new("ACME");

    public ValueTask DisposeAsync() => _projections.DisposeAsync();

    [Fact]
    public async Task Les_soldes_cumulent_debits_et_credits_par_compte()
    {
        await _projections.DeliverAsync(
            _acme.Posted(_march2, Debit("1100", 114.98m), Credit("4000", 100m), Credit("2310", 5m), Credit("2320", 9.98m)),
            _acme.Posted(_march9, Debit("1000", 114.98m), Credit("1100", 114.98m)));

        var balances = _projections.Rows<BalanceKey, BalanceRow>(ProjectionTables.AccountBalances);

        Assert.Equal(new BalanceRow(114.98m, 114.98m), balances[new BalanceKey("ACME", "1100", Currency)]);
        Assert.Equal(new BalanceRow(114.98m, 0m), balances[new BalanceKey("ACME", "1000", Currency)]);
        Assert.Equal(new BalanceRow(0m, 100m), balances[new BalanceKey("ACME", "4000", Currency)]);
        Assert.Equal(5, balances.Count);
    }

    [Fact]
    public async Task Plusieurs_lignes_du_meme_compte_sont_regroupees()
    {
        await _projections.DeliverAsync(_acme.Posted(_march2, Debit("1000", 10m), Debit("1000", 5.5m), Credit("4000", 15.5m)));

        Assert.Equal(new BalanceRow(15.5m, 0m), _projections.Rows<BalanceKey, BalanceRow>(ProjectionTables.AccountBalances)[new BalanceKey("ACME", "1000", Currency)]);
    }

    [Fact]
    public async Task Une_contre_passation_annule_l_effet_de_l_ecriture_sur_les_soldes()
    {
        var entryId = Guid.CreateVersion7();
        await _projections.DeliverAsync(
            _acme.Posted(entryId, _march2, Debit("1000", 40m), Credit("4000", 40m)),
            _acme.Reversed(entryId, _april3));

        var cash = _projections.Rows<BalanceKey, BalanceRow>(ProjectionTables.AccountBalances)[new BalanceKey("ACME", "1000", Currency)];
        Assert.Equal(0m, cash.DebitTotal - cash.CreditTotal);
    }

    [Fact]
    public async Task Les_lignes_de_compte_contraint_ne_sont_pas_comptees_deux_fois()
    {
        var entryId = Guid.CreateVersion7();
        var cash = Debit("1000", 40m);
        await _projections.DeliverAsync(
            _acme.Posted(entryId, _march2, cash, Credit("4000", 40m)),
            _acme.AccountLine(entryId, _march2, cash, 40m));

        Assert.Equal(new BalanceRow(40m, 0m), _projections.Rows<BalanceKey, BalanceRow>(ProjectionTables.AccountBalances)[new BalanceKey("ACME", "1000", Currency)]);
    }

    [Fact]
    public async Task Le_grand_livre_garde_l_ecriture_ses_lignes_numerotees_et_ses_metadonnees()
    {
        var entryId = Guid.CreateVersion7();
        var posted = _acme.Posted(entryId, _march2, Debit("1100", 114.98m), Credit("4000", 100m), Credit("2310", 5m), Credit("2320", 9.98m));

        await _projections.DeliverAsync(posted);

        var entry = _projections.Rows<Guid, JournalEntryRow>(ProjectionTables.JournalEntries)[entryId];
        Assert.Equal(new JournalEntryRow("ACME", _march2, "2026-03", $"Écriture {entryId}", null, null, posted.GlobalSequence, posted.RecordedAt, Actor), entry);
        var lines = _projections.Rows<LedgerLineKey, LedgerLineRow>(ProjectionTables.LedgerLines);
        Assert.Equal(4, lines.Count);
        Assert.Equal(
            new LedgerLineRow("ACME", "2320", "Credit", 9.98m, Currency, _march2, "2026-03", posted.GlobalSequence),
            lines[new LedgerLineKey(entryId, 4)]);
    }

    [Fact]
    public async Task Une_contre_passation_est_une_ecriture_liee_a_l_ecriture_d_origine()
    {
        var entryId = Guid.CreateVersion7();
        var reversalId = Guid.CreateVersion7();
        await _projections.DeliverAsync(
            _acme.Posted(entryId, _march2, Debit("1000", 40m), Credit("4000", 40m)),
            _acme.Reversed(entryId, _april3, reversalId));

        var entries = _projections.Rows<Guid, JournalEntryRow>(ProjectionTables.JournalEntries);
        Assert.Equal(reversalId, entries[entryId].ReversedBy);
        Assert.Equal(entryId, entries[reversalId].ReversalOf);
        Assert.Equal("2026-04", entries[reversalId].Period);
        Assert.Equal("Credit", _projections.Rows<LedgerLineKey, LedgerLineRow>(ProjectionTables.LedgerLines)[new LedgerLineKey(reversalId, 1)].Side);
    }

    [Fact]
    public async Task Une_contre_passation_d_une_ecriture_absente_echoue_sans_rien_ecrire()
    {
        var entryId = Guid.CreateVersion7();
        _acme.Posted(entryId, _march2, Debit("1000", 40m), Credit("4000", 40m));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _projections.DeliverAsync(_acme.Reversed(entryId, _april3)));

        Assert.Empty(_projections.Rows<Guid, JournalEntryRow>(ProjectionTables.JournalEntries));
        Assert.Empty(_projections.Rows<BalanceKey, BalanceRow>(ProjectionTables.AccountBalances));
        Assert.Empty(_projections.Rows<ConsumerOffsetKey, long>(ProjectionTables.ConsumerOffsets));
    }

    [Fact]
    public async Task Le_plan_comptable_garde_l_etat_courant_de_chaque_compte()
    {
        await _projections.DeliverAsync(
            _acme.AccountDefined("1000", "Encaisse", noNegativeBalance: true),
            _acme.AccountDefined("2310", "TPS à percevoir", normalSide: "Credit"),
            _acme.AccountDefined("1000", "Encaisse principale", isActive: false, noNegativeBalance: true));

        var chart = _projections.Rows<AccountKey, ChartAccount>(ProjectionTables.ChartOfAccounts);
        Assert.Equal(2, chart.Count);
        var cash = chart[new AccountKey("ACME", "1000")];
        Assert.Equal(("Encaisse principale", false, true, 3L), (cash.Name, cash.IsActive, cash.NoNegativeBalance, cash.ChartVersion));
        Assert.Equal("Credit", chart[new AccountKey("ACME", "2310")].NormalSide);
    }

    [Fact]
    public async Task Une_periode_ouverte_puis_scellee_porte_l_empreinte_du_sceau()
    {
        var opened = _acme.Opened("2026-03");
        await _projections.DeliverAsync(opened);
        var open = _projections.Rows<PeriodKey, PeriodRow>(ProjectionTables.Periods)[new PeriodKey("ACME", "2026-03")];
        Assert.Equal(new PeriodRow(PeriodsProjection.Open, 1, opened.RecordedAt, null, null, null), open);

        await _projections.DeliverAsync(_acme.Posted(_march2, Debit("1000", 1m), Credit("4000", 1m)));
        var sealedEvent = _acme.Sealed("2026-03", merkleRoot: "ab12");
        await _projections.DeliverAsync(sealedEvent);

        var sealedRow = _projections.Rows<PeriodKey, PeriodRow>(ProjectionTables.Periods)[new PeriodKey("ACME", "2026-03")];
        Assert.Equal(new PeriodRow(PeriodsProjection.Sealed, 2, opened.RecordedAt, sealedEvent.RecordedAt, 2, "ab12"), sealedRow);
    }

    [Fact]
    public async Task L_ordre_des_evenements_du_journal_est_celui_de_leur_sequence()
    {
        var first = _acme.Opened("2026-03");
        var second = _acme.Opened("2026-04");

        await _projections.DeliverAsync(second, first);

        Assert.Equal(
            ["2026-03", "2026-04"],
            (await _projections.Journal.ReadAfterAsync(0, 10, TestContext.Current.CancellationToken)).Select(e => e.Period));
        Assert.Equal(2, (await _projections.Journal.ReadAfterAsync(0, 10, TestContext.Current.CancellationToken)).Count);
        Assert.Single(await _projections.Journal.ReadAfterAsync(first.GlobalSequence, 10, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Une_ligne_de_sens_ou_de_montant_invalide_est_une_erreur_de_decodage()
    {
        Assert.Throws<EventDecodingException>(() => JournalLines.Movements([Debit("1000", 1m) with { Side = "Gauche" }]));
        Assert.Throws<EventDecodingException>(() => JournalLines.ParseAmount(new Contracts.Events.V1.MoneyV1("1,5", Currency)));
        Assert.Throws<EventDecodingException>(() => JournalLines.ParseAmount(new Contracts.Events.V1.MoneyV1("1e3", Currency)));
        Assert.Equal(-2.5m, JournalLines.ParseAmount(new Contracts.Events.V1.MoneyV1("-2.50", Currency)));
    }
}
