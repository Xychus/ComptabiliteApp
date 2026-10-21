using System.Globalization;
using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Events;

namespace Ledger.Projections.Application.Entries;

/// <summary>Mouvement d'un compte : débits et crédits cumulés, dans la devise de la ligne.</summary>
public sealed record AccountMovement(string AccountId, string Currency, decimal Debit, decimal Credit);

/// <summary>Lecture des lignes d'écriture des contrats : montants en chaîne décimale, sens <c>Debit</c> / <c>Credit</c>.</summary>
public static class JournalLines
{
    public const string Debit = "Debit";
    public const string Credit = "Credit";

    /// <summary>Montant d'une chaîne décimale invariante ; jamais un nombre à virgule flottante.</summary>
    public static decimal ParseAmount(MoneyV1 money)
    {
        ArgumentNullException.ThrowIfNull(money);
        return decimal.TryParse(money.Amount, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var amount)
            ? amount
            : throw new EventDecodingException($"Montant invalide : « {money.Amount} ».");
    }

    public static bool IsDebit(string side) => side switch
    {
        Debit => true,
        Credit => false,
        _ => throw new EventDecodingException($"Sens de ligne invalide : « {side} »."),
    };

    /// <summary>Mouvements d'une écriture, regroupés par compte et devise, dans l'ordre de première apparition.</summary>
    public static IReadOnlyList<AccountMovement> Movements(IEnumerable<JournalLineV1> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var totals = new Dictionary<(string Account, string Currency), (decimal Debit, decimal Credit)>();
        var order = new List<(string Account, string Currency)>();
        foreach (var line in lines)
        {
            var key = (line.AccountId, line.Amount.Currency);
            if (!totals.TryGetValue(key, out var total))
            {
                order.Add(key);
            }

            var amount = ParseAmount(line.Amount);
            totals[key] = IsDebit(line.Side) ? (total.Debit + amount, total.Credit) : (total.Debit, total.Credit + amount);
        }

        return [.. order.Select(key => new AccountMovement(key.Account, key.Currency, totals[key].Debit, totals[key].Credit))];
    }
}
