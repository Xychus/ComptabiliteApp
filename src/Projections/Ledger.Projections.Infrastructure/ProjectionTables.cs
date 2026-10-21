using Ledger.Projections.Application.Processing;

namespace Ledger.Projections.Infrastructure;

/// <summary>
/// Tables de chaque projection (migration 0007). Une reconstruction crée les mêmes tables avec le
/// suffixe <see cref="RebuildSuffix"/>, puis les met en service par renommage (ADR 0016). Les
/// noms sont des constantes de ce fichier, jamais des entrées.
/// </summary>
public static class ProjectionTables
{
    public const string Schema = "projections";
    public const string RebuildSuffix = "__rebuild";
    public const string RetiredSuffix = "__retired";
    public const string AppliedSuffix = "_applied";

    public const string AccountBalances = "account_balances";
    public const string JournalEntries = "journal_entries";
    public const string LedgerLines = "ledger_lines";
    public const string ChartOfAccounts = "chart_of_accounts";
    public const string Periods = "periods";
    public const string PeriodSnapshots = "period_snapshots";
    public const string TaxProjection = "tax_projection";
    public const string Positions = "projection_positions";
    public const string ConsumerOffsets = "consumer_offsets";

    private static readonly Dictionary<string, string[]> _dataTables = new(StringComparer.Ordinal)
    {
        [ProjectionNames.AccountBalances] = [AccountBalances],
        [ProjectionNames.LedgerEntries] = [JournalEntries, LedgerLines],
        [ProjectionNames.ChartOfAccounts] = [ChartOfAccounts],
        [ProjectionNames.Periods] = [Periods],
        [ProjectionNames.PeriodSnapshots] = [PeriodSnapshots],
        [ProjectionNames.TaxProjection] = [TaxProjection],
    };

    /// <summary>Table des événements appliqués d'une projection.</summary>
    public static string Applied(string projection) => projection + AppliedSuffix;

    /// <summary>Tables de données et table des événements appliqués d'une projection.</summary>
    public static IReadOnlyList<string> Of(string projection) =>
        _dataTables.TryGetValue(projection, out var tables)
            ? [.. tables, Applied(projection)]
            : throw new ArgumentException($"Projection inconnue : {projection}.", nameof(projection));

    /// <summary>Clé de la position d'une projection, en service ou en reconstruction.</summary>
    public static string Slot(string projection, ProjectionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.IsRebuild ? projection + RebuildSuffix : projection;
    }

    /// <summary>Nom physique d'une table pour la cible de la session.</summary>
    public static string Physical(string table, ProjectionTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.IsRebuild ? table + RebuildSuffix : table;
    }
}
