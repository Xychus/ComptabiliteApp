using Ledger.Projections.Application.Balances;
using Ledger.Projections.Application.Chart;
using Ledger.Projections.Application.Entries;
using Ledger.Projections.Application.Periods;
using Ledger.Projections.Application.Snapshots;
using Ledger.Projections.Application.Taxes;

namespace Ledger.Projections.Infrastructure.InMemory;

// Lignes des tables en mémoire : mêmes colonnes que le schéma projections (migration 0007).

public sealed record BalanceKey(string EntityId, string AccountId, string Currency);

public sealed record BalanceRow(decimal DebitTotal, decimal CreditTotal);

public sealed record JournalEntryRow(
    string EntityId,
    DateOnly BusinessDate,
    string Period,
    string? Description,
    Guid? ReversalOf,
    Guid? ReversedBy,
    long GlobalSequence,
    DateTimeOffset RecordedAt,
    string Actor);

public sealed record LedgerLineKey(Guid EntryId, int LineNumber);

public sealed record LedgerLineRow(string EntityId, string AccountId, string Side, decimal Amount, string Currency, DateOnly BusinessDate, string Period, long GlobalSequence);

public sealed record AccountKey(string EntityId, string AccountId);

public sealed record PeriodKey(string EntityId, string Period);

public sealed record PeriodRow(string Status, long Version, DateTimeOffset? OpenedAt, DateTimeOffset? SealedAt, long? EventCount, string? MerkleRoot);

public sealed record SnapshotKey(string EntityId, string Period, string AccountId, string Currency);

public sealed record SnapshotRow(decimal Debits, decimal Credits, long SealSequence, long EventCount);

public sealed record TaxKey(string EntityId, string Period, string Currency);

public sealed record TaxRow(decimal GstCollected, decimal GstPaid, decimal QstCollected, decimal QstPaid);

public sealed class InMemoryAccountBalanceStore(InMemoryProjectionSession session) : IAccountBalanceStore
{
    public Task AddAsync(string entityId, AccountMovement movement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(movement);
        var rows = session.Rows<BalanceKey, BalanceRow>(ProjectionTables.AccountBalances);
        var key = new BalanceKey(entityId, movement.AccountId, movement.Currency);
        var current = rows.GetValueOrDefault(key) ?? new BalanceRow(0m, 0m);
        session.Save(ProjectionTables.AccountBalances, rows.SetItem(key, new BalanceRow(current.DebitTotal + movement.Debit, current.CreditTotal + movement.Credit)));
        return Task.CompletedTask;
    }
}

public sealed class InMemoryLedgerEntryStore(InMemoryProjectionSession session) : ILedgerEntryStore
{
    public Task AddAsync(LedgerEntry entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var entries = session.Rows<Guid, JournalEntryRow>(ProjectionTables.JournalEntries);
        var lines = session.Rows<LedgerLineKey, LedgerLineRow>(ProjectionTables.LedgerLines);

        // Mêmes clés primaires qu'en base : un doublon est une erreur.
        session.Save(ProjectionTables.JournalEntries, entries.Add(
            entry.EntryId,
            new JournalEntryRow(entry.EntityId, entry.BusinessDate, entry.Period, entry.Description, entry.ReversalOf, null, entry.GlobalSequence, entry.RecordedAt, entry.Actor)));
        session.Save(ProjectionTables.LedgerLines, lines.AddRange(entry.Lines.Select(line => KeyValuePair.Create(
            new LedgerLineKey(entry.EntryId, line.LineNumber),
            new LedgerLineRow(entry.EntityId, line.AccountId, line.Side, line.Amount, line.Currency, entry.BusinessDate, entry.Period, entry.GlobalSequence)))));
        return Task.CompletedTask;
    }

    public Task MarkReversedAsync(Guid entryId, Guid reversalEntryId, CancellationToken cancellationToken)
    {
        var entries = session.Rows<Guid, JournalEntryRow>(ProjectionTables.JournalEntries);
        var entry = entries.GetValueOrDefault(entryId) ?? throw new InvalidOperationException($"Écriture contre-passée {entryId} absente de la projection.");
        session.Save(ProjectionTables.JournalEntries, entries.SetItem(entryId, entry with { ReversedBy = reversalEntryId }));
        return Task.CompletedTask;
    }
}

public sealed class InMemoryChartOfAccountsStore(InMemoryProjectionSession session) : IChartOfAccountsStore
{
    public Task SaveAsync(ChartAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        var rows = session.Rows<AccountKey, ChartAccount>(ProjectionTables.ChartOfAccounts);
        session.Save(ProjectionTables.ChartOfAccounts, rows.SetItem(new AccountKey(account.EntityId, account.AccountId), account));
        return Task.CompletedTask;
    }
}

public sealed class InMemoryPeriodStatusStore(InMemoryProjectionSession session) : IPeriodStatusStore
{
    public Task OpenAsync(PeriodOpening opening, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(opening);
        var rows = session.Rows<PeriodKey, PeriodRow>(ProjectionTables.Periods);
        var key = new PeriodKey(opening.EntityId, opening.Period);
        var row = rows.TryGetValue(key, out var current)
            ? current with { Version = opening.Version, OpenedAt = opening.OpenedAt }
            : new PeriodRow(PeriodsProjection.Open, opening.Version, opening.OpenedAt, null, null, null);
        session.Save(ProjectionTables.Periods, rows.SetItem(key, row));
        return Task.CompletedTask;
    }

    public Task SealAsync(PeriodSeal seal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(seal);
        var rows = session.Rows<PeriodKey, PeriodRow>(ProjectionTables.Periods);
        var key = new PeriodKey(seal.EntityId, seal.Period);
        var openedAt = rows.GetValueOrDefault(key)?.OpenedAt;
        session.Save(ProjectionTables.Periods, rows.SetItem(key, new PeriodRow(PeriodsProjection.Sealed, seal.Version, openedAt, seal.SealedAt, seal.EventCount, seal.MerkleRoot)));
        return Task.CompletedTask;
    }
}

public sealed class InMemoryPeriodSnapshotStore(InMemoryProjectionSession session) : IPeriodSnapshotStore
{
    public Task SaveAsync(PeriodSnapshot snapshot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var rows = session.Rows<SnapshotKey, SnapshotRow>(ProjectionTables.PeriodSnapshots);
        session.Save(ProjectionTables.PeriodSnapshots, rows.AddRange(snapshot.Lines.Select(line => KeyValuePair.Create(
            new SnapshotKey(snapshot.EntityId, snapshot.Period, line.AccountId, line.Currency),
            new SnapshotRow(line.Debits, line.Credits, snapshot.SealSequence, snapshot.EventCount)))));
        return Task.CompletedTask;
    }
}

public sealed class InMemoryTaxProjectionStore(InMemoryProjectionSession session) : ITaxProjectionStore
{
    public Task AddAsync(TaxMovement movement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(movement);
        var rows = session.Rows<TaxKey, TaxRow>(ProjectionTables.TaxProjection);
        var key = new TaxKey(movement.EntityId, movement.Period, movement.Currency);
        var current = rows.GetValueOrDefault(key) ?? new TaxRow(0m, 0m, 0m, 0m);
        var updated = movement.Kind switch
        {
            TaxKind.GstCollected => current with { GstCollected = current.GstCollected + movement.Amount },
            TaxKind.GstPaid => current with { GstPaid = current.GstPaid + movement.Amount },
            TaxKind.QstCollected => current with { QstCollected = current.QstCollected + movement.Amount },
            TaxKind.QstPaid => current with { QstPaid = current.QstPaid + movement.Amount },
            _ => throw new ArgumentOutOfRangeException(nameof(movement), movement.Kind, "Rôle fiscal inconnu."),
        };
        session.Save(ProjectionTables.TaxProjection, rows.SetItem(key, updated));
        return Task.CompletedTask;
    }
}
