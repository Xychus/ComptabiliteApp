using Ledger.Command.Domain.Periods;
using Ledger.SharedKernel;

namespace Ledger.Command.Application.EventStore;

/// <summary>
/// Noms des flux (ADR 0003 et 0004). Les composants libres sont échappés (<c>%</c> → <c>%25</c>,
/// <c>-</c> → <c>%2D</c>) pour que deux couples distincts ne donnent jamais le même nom.
/// </summary>
public static class StreamIds
{
    public static string Entry(EntryId entryId) => $"entry-{entryId.Value:D}";

    public static string Account(EntityId entity, AccountId account) => $"account-{Escape(entity.Value)}-{Escape(account.Value)}";

    public static string Period(EntityId entity, PeriodId period) => $"period-{Escape(entity.Value)}-{period}";

    private static string Escape(string component) =>
        component.Replace("%", "%25", StringComparison.Ordinal).Replace("-", "%2D", StringComparison.Ordinal);
}
