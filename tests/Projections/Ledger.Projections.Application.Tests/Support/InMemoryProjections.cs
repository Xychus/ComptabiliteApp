using System.Collections.Immutable;
using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Processing;
using Ledger.Projections.Application.Rebuild;
using Ledger.Projections.Application.Taxes;
using Ledger.Projections.Infrastructure;
using Ledger.Projections.Infrastructure.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Projections.Application.Tests.Support;

/// <summary>
/// Worker de projections complet sur l'implémentation en mémoire : journal, livraison par le
/// transport (offsets croissants d'une partition) et reconstruction.
/// </summary>
public sealed class InMemoryProjections : IAsyncDisposable
{
    public const string ConsumerGroup = "tests";
    public const string Topic = "ledger.events";

    private readonly ServiceProvider _services;
    private long _nextOffset;

    public InMemoryProjections(TaxAccountMap? taxAccounts = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedgerProjections(taxAccounts ?? TaxAccountMap.Empty);
        services.AddInMemoryProjectionStore(Journal);
        _services = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public InMemoryJournal Journal { get; } = new();

    public InMemoryProjectionDatabase Database => _services.GetRequiredService<InMemoryProjectionDatabase>();

    public IServiceProvider Services => _services;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Événements validés dans le journal, pas encore livrés.</summary>
    public void Record(params EventEnvelopeV1[] events)
    {
        foreach (var envelope in events)
        {
            Journal.Append(envelope);
        }
    }

    /// <summary>Validés puis livrés, dans l'ordre donné.</summary>
    public async Task DeliverAsync(params EventEnvelopeV1[] events)
    {
        Record(events);
        await RedeliverAsync(events);
    }

    /// <summary>Livrés (ou relivrés) sans être ajoutés au journal : nouvel offset à chaque livraison.</summary>
    public async Task RedeliverAsync(params EventEnvelopeV1[] events)
    {
        var pump = _services.GetRequiredService<ProjectionPump>();
        foreach (var envelope in events)
        {
            await pump.ProcessAsync(new SourceMessage(envelope, new SourcePosition(ConsumerGroup, Topic, 0, _nextOffset++)), Ct);
        }
    }

    public Task<RebuildReport> RebuildAsync(string projection) =>
        _services.GetRequiredService<ProjectionRebuilder>().RebuildAsync(projection, Ct);

    public ImmutableDictionary<TKey, TRow> Rows<TKey, TRow>(string table)
        where TKey : notnull => Database.Rows<TKey, TRow>(table);

    /// <summary>Contenu canonique des tables d'une projection (données et événements appliqués).</summary>
    public string Dump(string projection) =>
        string.Join(
            "\n",
            ProjectionTables.Of(projection).Select(table => $"[{table}]\n" + string.Join("\n", RowsOf(table).Order(StringComparer.Ordinal))));

    public ValueTask DisposeAsync() => _services.DisposeAsync();

    private IEnumerable<string> RowsOf(string table) => table switch
    {
        ProjectionTables.AccountBalances => Format(Rows<BalanceKey, BalanceRow>(table)),
        ProjectionTables.JournalEntries => Format(Rows<Guid, JournalEntryRow>(table)),
        ProjectionTables.LedgerLines => Format(Rows<LedgerLineKey, LedgerLineRow>(table)),
        ProjectionTables.ChartOfAccounts => Format(Rows<AccountKey, Application.Chart.ChartAccount>(table)),
        ProjectionTables.Periods => Format(Rows<PeriodKey, PeriodRow>(table)),
        ProjectionTables.PeriodSnapshots => Format(Rows<SnapshotKey, SnapshotRow>(table)),
        ProjectionTables.TaxProjection => Format(Rows<TaxKey, TaxRow>(table)),
        _ when table.EndsWith(ProjectionTables.AppliedSuffix, StringComparison.Ordinal) => Format(Rows<Guid, long>(table)),
        _ => throw new ArgumentException($"Table inconnue : {table}.", nameof(table)),
    };

    private static IEnumerable<string> Format<TKey, TRow>(ImmutableDictionary<TKey, TRow> rows)
        where TKey : notnull => rows.Select(row => $"{row.Key} => {row.Value}");
}
