using FsCheck;
using FsCheck.Fluent;
using Ledger.Command.Domain.Accounts;
using Ledger.Command.Domain.Entries;
using Ledger.SharedKernel;
using static Ledger.Command.Domain.Tests.Support.TestLedger;

namespace Ledger.Command.Domain.Tests.Properties;

/// <summary>Lignes quelconques : valides ou non (comptes inactifs, montants nuls, devises mêlées…).</summary>
public sealed record AnyEntry(EquatableList<JournalLine> Lines);

/// <summary>Écriture valide par construction : comptes CAD actifs, montants au cent, équilibrée.</summary>
public sealed record BalancedEntry(EquatableList<JournalLine> Lines);

/// <summary>Suite de mouvements (sens, montant) sur le compte contraint « Encaisse ».</summary>
public sealed record CashMovements(IReadOnlyList<(Side Side, decimal Amount)> Moves);

public static class LedgerGenerators
{
    private static readonly AccountInfo[] _anyAccounts = [Cash, Receivables, UsdBank, GstPayable, QstPayable, Sales, Closed];
    private static readonly AccountInfo[] _validCadAccounts = [Cash, Receivables, GstPayable, QstPayable, Sales];

    public static Arbitrary<AnyEntry> AnyEntry() =>
        Gen.OneOf(RandomLines(), BalancedLines()).Select(lines => new AnyEntry(lines)).ToArbitrary();

    public static Arbitrary<BalancedEntry> BalancedEntry() =>
        BalancedLines().Select(lines => new BalancedEntry(lines)).ToArbitrary();

    public static Arbitrary<CashMovements> CashMovements() =>
        (from side in Gen.Elements(Side.Debit, Side.Credit)
         from cents in Gen.Choose(1, 20_000)
         select (side, cents / 100m))
        .ArrayOf()
        .Select(moves => new CashMovements(moves))
        .ToArbitrary();

    private static Gen<EquatableList<JournalLine>> RandomLines() =>
        (from account in Gen.Elements(_anyAccounts)
         from side in Gen.Elements(Side.Debit, Side.Credit)
         from milliCents in Gen.Choose(-1_000, 10_000_000)
         select new JournalLine(account.Id, side, new Money(milliCents / 1000m, account.Currency)))
        .ArrayOf()
        .Select(lines => EquatableList.Of(lines));

    private static Gen<EquatableList<JournalLine>> BalancedLines() =>
        from credits in (from account in Gen.Elements(_validCadAccounts)
                         from cents in Gen.Choose(1, 10_000_000)
                         select new JournalLine(account.Id, Side.Credit, Cad(cents / 100m))).NonEmptyListOf()
        let totalCents = (int)(credits.Sum(c => c.Amount.Amount) * 100)
        from split in Gen.Choose(0, totalCents - 1)
        from firstDebit in Gen.Elements(_validCadAccounts)
        from secondDebit in Gen.Elements(_validCadAccounts)
        let debits = split == 0
            ? new[] { new JournalLine(firstDebit.Id, Side.Debit, Cad(totalCents / 100m)) }
            : [new JournalLine(firstDebit.Id, Side.Debit, Cad(split / 100m)), new JournalLine(secondDebit.Id, Side.Debit, Cad((totalCents - split) / 100m))]
        from lines in Gen.Shuffle(debits.Concat(credits).ToArray())
        select EquatableList.Of(lines);
}
