using FsCheck.Xunit;
using Ledger.Command.Domain.ConstrainedAccounts;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Periods;
using Ledger.SharedKernel;
using static Ledger.Command.Domain.Tests.Support.TestLedger;

namespace Ledger.Command.Domain.Tests.Properties;

/// <summary>Invariants universels du grand livre, vérifiés sur 1 000 cas générés chacun.</summary>
[Properties(MaxTest = 1000, Arbitrary = [typeof(LedgerGenerators)])]
public sealed class LedgerProperties
{
    /// <summary>(a) Toute écriture acceptée est équilibrée, devise par devise.</summary>
    [Property]
    public bool Toute_ecriture_acceptee_est_equilibree(AnyEntry entry)
    {
        var result = Post(entry.Lines, EntryId.New());

        return result.IsFailure || IsBalancedPerCurrency(((JournalEntryPosted)result.Value[0]).Lines);
    }

    /// <summary>Complément de (a) : une écriture valide et équilibrée est toujours acceptée.</summary>
    [Property]
    public bool Toute_ecriture_valide_et_equilibree_est_acceptee(BalancedEntry entry) =>
        Post(entry.Lines, EntryId.New()).IsSuccess;

    /// <summary>(b) Aucune suite de mouvements acceptés ne rend un compte contraint négatif.</summary>
    [Property]
    public bool Aucune_suite_acceptee_ne_rend_un_compte_contraint_negatif(CashMovements movements)
    {
        var state = ConstrainedAccountState.Initial(Acme, Cash);
        var expected = 0m;
        foreach (var (side, amount) in movements.Moves)
        {
            var delta = side == Side.Debit ? amount : -amount;
            var result = ConstrainedAccountDecider.Decide(state, new ApplyAccountLine(EntryId.New(), new JournalLine(Cash.Id, side, Cad(amount))));
            if (result.IsSuccess != (expected + delta >= 0m))
            {
                return false;
            }

            if (result.IsSuccess)
            {
                state = result.Value.Aggregate(state, (s, e) => s.Evolve(e));
                expected += delta;
            }

            if (state.Balance.IsNegative)
            {
                return false;
            }
        }

        return state.Balance == Cad(expected);
    }

    /// <summary>(c) Une écriture suivie de sa contre-passation a un effet nul sur chaque compte.</summary>
    [Property]
    public bool Ecriture_et_contre_passation_ont_un_effet_nul(BalancedEntry entry)
    {
        var entryId = EntryId.New();
        var posted = Post(entry.Lines, entryId);
        var state = posted.Value.Aggregate(JournalEntryState.Initial(entryId), (s, e) => s.Evolve(e));
        var reversed = JournalEntryDecider.Decide(state, new ReverseEntry(entryId, EntryId.New(), March15, null), OpenMarch);

        var allLines = ((JournalEntryPosted)posted.Value[0]).Lines.Concat(((JournalEntryReversed)reversed.Value[0]).Lines);
        return allLines
            .GroupBy(line => line.Account)
            .All(account => account.Sum(line => line.Side == Side.Debit ? line.Amount.Amount : -line.Amount.Amount) == 0m);
    }

    private static Result<IReadOnlyList<IDomainEvent>> Post(EquatableList<JournalLine> lines, EntryId entryId) =>
        JournalEntryDecider.Default.Decide(
            JournalEntryState.Initial(entryId),
            new PostEntry(entryId, Acme, March15, null, lines),
            Period(March, PeriodStatus.Open),
            Chart);

    private static bool IsBalancedPerCurrency(IEnumerable<JournalLine> lines) =>
        lines.GroupBy(line => line.Amount.Currency).All(group =>
            group.Where(l => l.Side == Side.Debit).Sum(l => l.Amount.Amount) == group.Where(l => l.Side == Side.Credit).Sum(l => l.Amount.Amount));
}
