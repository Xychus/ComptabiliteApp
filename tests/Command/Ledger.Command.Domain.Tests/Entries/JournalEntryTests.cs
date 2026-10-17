using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Periods;
using Ledger.Command.Domain.Tests.Support;
using Ledger.SharedKernel;
using static Ledger.Command.Domain.Tests.Support.TestLedger;

namespace Ledger.Command.Domain.Tests.Entries;

public sealed class PostEntryTests
{
    private static readonly EntryId _entryId = EntryId.New();
    private static readonly EquatableList<JournalLine> _sale = Lines(Debit(Receivables, 105m), Credit(Sales, 100m), Credit(GstPayable, 5m));

    private static Scenario<JournalEntryState> Entry() =>
        new(JournalEntryState.Initial(_entryId), (state, e) => state.Evolve(e));

    private static PostEntry Post(EquatableList<JournalLine>? lines = null, DateOnly? date = null) =>
        new(_entryId, Acme, date ?? March15, "Vente au comptant", lines ?? _sale);

    [Fact]
    public void Passer_une_ecriture_valide_dans_une_periode_ouverte() =>
        Entry()
            .When(s => JournalEntryDecider.Default.Decide(s, Post(), OpenMarch, Chart))
            .Then(new JournalEntryPosted(_entryId, Acme, March15, March, "Vente au comptant", _sale));

    [Fact]
    public void Repasser_la_meme_ecriture_est_rejete() =>
        Entry()
            .Given(new JournalEntryPosted(_entryId, Acme, March15, March, "Vente au comptant", _sale))
            .When(s => JournalEntryDecider.Default.Decide(s, Post(), OpenMarch, Chart))
            .ThenRejected(EntryErrors.AlreadyPostedCode);

    [Fact]
    public void Ecriture_dans_une_periode_scellee_rejetee() =>
        Entry()
            .When(s => JournalEntryDecider.Default.Decide(s, Post(), Period(March, PeriodStatus.Sealed), Chart))
            .ThenRejected(PeriodErrors.SealedCode);

    [Fact]
    public void Ecriture_dans_une_periode_non_ouverte_rejetee() =>
        Entry()
            .When(s => JournalEntryDecider.Default.Decide(s, Post(), Period(March, PeriodStatus.NotOpened), Chart))
            .ThenRejected(PeriodErrors.NotOpenCode);

    [Fact]
    public void Ecriture_desequilibree_rejetee_par_les_regles() =>
        Entry()
            .When(s => JournalEntryDecider.Default.Decide(s, Post(Lines(Debit(Receivables, 105m), Credit(Sales, 100m))), OpenMarch, Chart))
            .ThenRejected(EntryErrors.NotBalancedCode);

    [Fact]
    public void Etat_de_periode_d_une_autre_periode_est_un_bogue() =>
        Assert.Throws<ArgumentException>(() =>
            JournalEntryDecider.Default.Decide(JournalEntryState.Initial(_entryId), Post(), Period(April, PeriodStatus.Open), Chart));
}

public sealed class ReverseEntryTests
{
    private static readonly EntryId _entryId = EntryId.New();
    private static readonly EntryId _reversalId = EntryId.New();
    private static readonly DateOnly _april2 = new(2026, 4, 2);
    private static readonly JournalEntryPosted _posted =
        new(_entryId, Acme, March15, March, "Vente", Lines(Debit(Receivables, 105m), Credit(Sales, 100m), Credit(GstPayable, 5m)));

    private static Scenario<JournalEntryState> Entry() =>
        new(JournalEntryState.Initial(_entryId), (state, e) => state.Evolve(e));

    private static ReverseEntry Reverse(EntryId? reversalId = null) => new(_entryId, reversalId ?? _reversalId, _april2, "Annulation");

    [Fact]
    public void Contre_passer_produit_les_lignes_inversees_dans_la_periode_de_contre_passation() =>
        Entry()
            .Given(_posted)
            .When(s => JournalEntryDecider.Decide(s, Reverse(), Period(April, PeriodStatus.Open)))
            .Then(new JournalEntryReversed(
                _reversalId,
                _entryId,
                Acme,
                _april2,
                April,
                "Annulation",
                Lines(Credit(Receivables, 105m), Debit(Sales, 100m), Debit(GstPayable, 5m))));

    [Fact]
    public void Contre_passer_une_ecriture_inexistante_est_rejete() =>
        Entry()
            .When(s => JournalEntryDecider.Decide(s, Reverse(), Period(April, PeriodStatus.Open)))
            .ThenRejected(EntryErrors.NotFoundCode);

    [Fact]
    public void Contre_passer_deux_fois_est_rejete()
    {
        var error = Entry()
            .Given(_posted, new JournalEntryReversed(_reversalId, _entryId, Acme, _april2, April, null, Lines()))
            .When(s => JournalEntryDecider.Decide(s, Reverse(EntryId.New()), Period(April, PeriodStatus.Open)))
            .ThenRejected(EntryErrors.AlreadyReversedCode);

        Assert.Equal(_reversalId.ToString(), error.Details!["reversedBy"]);
    }

    [Fact]
    public void Identifiant_de_contre_passation_egal_a_l_original_rejete() =>
        Entry()
            .Given(_posted)
            .When(s => JournalEntryDecider.Decide(s, Reverse(_entryId), Period(April, PeriodStatus.Open)))
            .ThenRejected(EntryErrors.InvalidReversalIdCode);

    [Fact]
    public void Contre_passation_dans_une_periode_scellee_rejetee() =>
        Entry()
            .Given(_posted)
            .When(s => JournalEntryDecider.Decide(s, Reverse(), Period(April, PeriodStatus.Sealed)))
            .ThenRejected(PeriodErrors.SealedCode);

    [Fact]
    public void Un_evenement_etranger_ne_change_pas_l_etat()
    {
        var state = JournalEntryState.Initial(_entryId);

        Assert.Equal(state, state.Evolve(new PeriodOpened(Acme, March)));
        Assert.False(state.IsPosted);
    }
}
