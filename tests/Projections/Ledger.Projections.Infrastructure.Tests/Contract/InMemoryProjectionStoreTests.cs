using System.Collections.Immutable;
using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Chart;
using Ledger.Projections.Infrastructure.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Projections.Infrastructure.Tests.Contract;

/// <summary>Suite de contrat contre l'implémentation en mémoire.</summary>
public sealed class InMemoryProjectionStoreTests : ProjectionStoreContract
{
    protected override ProjectionBackend CreateBackend() => new InMemoryBackend();

    protected override Task<(decimal, decimal, decimal, decimal)?> TaxAsync(string period)
    {
        var rows = ((InMemoryBackend)Backend).Database.Rows<TaxKey, TaxRow>(ProjectionTables.TaxProjection);
        (decimal, decimal, decimal, decimal)? tax = rows.TryGetValue(new TaxKey(Backend.Entity, period, "CAD"), out var row)
            ? (row.GstCollected, row.GstPaid, row.QstCollected, row.QstPaid)
            : null;
        return Task.FromResult(tax);
    }

    private sealed class InMemoryBackend : ProjectionBackend
    {
        private readonly InMemoryJournal _journal = new();
        private long _sequence;

        public InMemoryProjectionDatabase Database => Services.GetRequiredService<InMemoryProjectionDatabase>();

        public override Task<EventEnvelopeV1> RecordAsync(EventEnvelopeV1 envelope)
        {
            // Comme le trigger de PostgreSQL : la séquence vient du serveur, pas de l'appelant.
            var stored = envelope with { GlobalSequence = Interlocked.Increment(ref _sequence) };
            _journal.Append(stored);
            return Task.FromResult(stored);
        }

        public override Task<string> DumpAsync(string projection, string entityId)
        {
            var entityEvents = _journal.ReadAfterAsync(0, int.MaxValue, Ct).Result.Where(e => e.EntityId == entityId).Select(e => e.EventId).ToHashSet();
            var lines = ProjectionTables.Of(projection).SelectMany(table => RowsOf(table, entityId, entityEvents).Order(StringComparer.Ordinal).Prepend($"[{table}]"));
            return Task.FromResult(string.Join("\n", lines));
        }

        public override Task<long?> PositionAsync(string slot) =>
            Task.FromResult(Database.Rows<string, ProjectionPosition>(ProjectionTables.Positions).GetValueOrDefault(slot)?.ThroughSequence);

        public override Task<(decimal Debit, decimal Credit)?> BalanceAsync(string entityId, string accountId)
        {
            (decimal, decimal)? balance = Database.Rows<BalanceKey, BalanceRow>(ProjectionTables.AccountBalances).TryGetValue(new BalanceKey(entityId, accountId, "CAD"), out var row)
                ? (row.DebitTotal, row.CreditTotal)
                : null;
            return Task.FromResult(balance);
        }

        protected override void AddStore(IServiceCollection services) => services.AddInMemoryProjectionStore(_journal);

        private IEnumerable<string> RowsOf(string table, string entityId, HashSet<Guid> entityEvents) => table switch
        {
            ProjectionTables.AccountBalances => Format(Database.Rows<BalanceKey, BalanceRow>(table), key => key.EntityId == entityId),
            ProjectionTables.JournalEntries => Format(Database.Rows<Guid, JournalEntryRow>(table), (_, row) => row.EntityId == entityId),
            ProjectionTables.LedgerLines => Format(Database.Rows<LedgerLineKey, LedgerLineRow>(table), (_, row) => row.EntityId == entityId),
            ProjectionTables.ChartOfAccounts => Format(Database.Rows<AccountKey, ChartAccount>(table), key => key.EntityId == entityId),
            ProjectionTables.Periods => Format(Database.Rows<PeriodKey, PeriodRow>(table), key => key.EntityId == entityId),
            ProjectionTables.PeriodSnapshots => Format(Database.Rows<SnapshotKey, SnapshotRow>(table), key => key.EntityId == entityId),
            ProjectionTables.TaxProjection => Format(Database.Rows<TaxKey, TaxRow>(table), key => key.EntityId == entityId),
            _ => Format(Database.Rows<Guid, long>(table), entityEvents.Contains),
        };

        private static IEnumerable<string> Format<TKey, TRow>(ImmutableDictionary<TKey, TRow> rows, Func<TKey, bool> keep)
            where TKey : notnull => Format(rows, (key, _) => keep(key));

        private static IEnumerable<string> Format<TKey, TRow>(ImmutableDictionary<TKey, TRow> rows, Func<TKey, TRow, bool> keep)
            where TKey : notnull => rows.Where(row => keep(row.Key, row.Value)).Select(row => $"{row.Key} => {row.Value}");
    }
}
