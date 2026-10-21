using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Processing;
using Ledger.Projections.Application.Tests.Support;
using Ledger.Projections.Infrastructure;
using Ledger.Projections.Infrastructure.InMemory;
using Ledger.Projections.Tests.Support;

namespace Ledger.Projections.Application.Tests.Properties;

/// <summary>Une écriture équilibrée générée : montants en cents, débits = crédits.</summary>
public sealed record GeneratedEntry(IReadOnlyList<(string Account, int Cents)> Debits, IReadOnlyList<(string Account, int Cents)> Credits, bool Reversed, bool Duplicated);

/// <summary>Journal généré : écritures en mars, contre-passations éventuelles en avril, relivraisons.</summary>
public sealed record GeneratedJournal(IReadOnlyList<GeneratedEntry> Entries);

public static class ProjectionGenerators
{
    private static readonly string[] _accounts = ["1000", "1100", "2310", "2320", "4000", "5100"];

    public static Arbitrary<GeneratedJournal> GeneratedJournal() =>
        Entry().ListOf().Select(entries => new GeneratedJournal([.. entries])).ToArbitrary();

    private static Gen<GeneratedEntry> Entry() =>
        from debits in Line().NonEmptyListOf()
        from creditAccount in Gen.Elements(_accounts)
        from split in Gen.Choose(0, 100)
        from reversed in Gen.Elements(true, false)
        from duplicated in Gen.Elements(true, false)
        let total = debits.Sum(d => d.Cents)
        let first = Math.Max(1, total * split / 100)
        select new GeneratedEntry(
            [.. debits],
            first >= total ? [(creditAccount, total)] : [(creditAccount, first), (_accounts[0], total - first)],
            reversed,
            duplicated);

    private static Gen<(string Account, int Cents)> Line() =>
        from account in Gen.Elements(_accounts)
        from cents in Gen.Choose(1, 5_000_000)
        select (account, cents);
}

/// <summary>Invariants des projections sur des journaux générés (200 cas chacun).</summary>
[Properties(MaxTest = 200, Arbitrary = [typeof(ProjectionGenerators)])]
public sealed class ProjectionProperties
{
    /// <summary>La balance de vérification est équilibrée, en incrémental comme après rejeu.</summary>
    [Property]
    public bool La_balance_est_equilibree_avant_et_apres_rejeu(GeneratedJournal journal) => Run(journal, async projections =>
    {
        var before = IsBalanced(projections);
        await projections.RebuildAsync(ProjectionNames.AccountBalances);
        return before && IsBalanced(projections);
    });

    /// <summary>Rejouer chaque projection donne exactement les tables obtenues en incrémental, relivraisons comprises.</summary>
    [Property]
    public bool Le_rejeu_egale_l_incremental(GeneratedJournal journal) => Run(journal, async projections =>
    {
        foreach (var projection in ProjectionNames.All)
        {
            var incremental = projections.Dump(projection);
            await projections.RebuildAsync(projection);
            if (incremental != projections.Dump(projection))
            {
                return false;
            }
        }

        return true;
    });

    /// <summary>Les relivraisons n'ont aucun effet : même résultat qu'une livraison unique de chaque événement.</summary>
    [Property]
    public bool Les_relivraisons_n_ont_aucun_effet(GeneratedJournal journal) => Run(journal, async projections =>
    {
        await using var once = new InMemoryProjections();
        await once.DeliverAsync(await ReadJournalAsync(projections));
        return ProjectionNames.All.All(projection => once.Dump(projection) == projections.Dump(projection));
    });

    private static bool Run(GeneratedJournal journal, Func<InMemoryProjections, Task<bool>> check) =>
        Task.Run(async () =>
        {
            await using var projections = new InMemoryProjections();
            await DeliverAsync(projections, journal);
            return await check(projections);
        }).GetAwaiter().GetResult();

    private static async Task DeliverAsync(InMemoryProjections projections, GeneratedJournal journal)
    {
        var acme = new JournalBuilder("ACME");
        foreach (var entry in journal.Entries)
        {
            var entryId = Guid.CreateVersion7();
            var posted = acme.Posted(
                entryId,
                new DateOnly(2026, 3, 2),
                [.. entry.Debits.Select(d => JournalBuilder.Debit(d.Account, d.Cents / 100m)), .. entry.Credits.Select(c => JournalBuilder.Credit(c.Account, c.Cents / 100m))]);
            await projections.DeliverAsync(posted);
            if (entry.Duplicated)
            {
                await projections.RedeliverAsync(posted);
            }

            if (entry.Reversed)
            {
                await projections.DeliverAsync(acme.Reversed(entryId, new DateOnly(2026, 4, 1)));
            }
        }
    }

    private static async Task<EventEnvelopeV1[]> ReadJournalAsync(InMemoryProjections projections) =>
        [.. await projections.Journal.ReadAfterAsync(0, int.MaxValue, CancellationToken.None)];

    private static bool IsBalanced(InMemoryProjections projections)
    {
        var balances = projections.Rows<BalanceKey, BalanceRow>(ProjectionTables.AccountBalances);
        return balances.Values.Sum(row => row.DebitTotal) == balances.Values.Sum(row => row.CreditTotal);
    }
}
