using System.Globalization;
using Ledger.SharedKernel;

namespace Ledger.Command.Domain.Entries;

/// <summary>Codes et fabriques des rejets liés aux écritures.</summary>
public static class EntryErrors
{
    public const string TooFewLinesCode = "EntryTooFewLines";
    public const string NonPositiveAmountCode = "EntryNonPositiveAmount";
    public const string InvalidPrecisionCode = "EntryInvalidAmountPrecision";
    public const string MixedCurrenciesCode = "EntryMixedCurrencies";
    public const string InactiveAccountCode = "EntryInactiveAccount";
    public const string AccountCurrencyMismatchCode = "EntryAccountCurrencyMismatch";
    public const string NotBalancedCode = "EntryNotBalanced";
    public const string AlreadyPostedCode = "EntryAlreadyPosted";
    public const string NotFoundCode = "EntryNotFound";
    public const string AlreadyReversedCode = "EntryAlreadyReversed";
    public const string InvalidReversalIdCode = "EntryInvalidReversalId";

    public static DomainError AlreadyPosted(EntryId entryId) =>
        DomainError.WithDetails(AlreadyPostedCode, $"L'écriture {entryId} existe déjà.", ("entryId", entryId.ToString()));

    public static DomainError NotFound(EntryId entryId) =>
        DomainError.WithDetails(NotFoundCode, $"L'écriture {entryId} n'existe pas.", ("entryId", entryId.ToString()));

    public static DomainError AlreadyReversed(EntryId entryId, EntryId reversedBy) =>
        DomainError.WithDetails(AlreadyReversedCode, $"L'écriture {entryId} est déjà contre-passée par {reversedBy}.", ("entryId", entryId.ToString()), ("reversedBy", reversedBy.ToString()));

    public static DomainError InvalidReversalId(EntryId entryId) =>
        DomainError.WithDetails(InvalidReversalIdCode, "L'identifiant de contre-passation doit différer de celui de l'écriture d'origine.", ("entryId", entryId.ToString()));

    /// <summary>Erreur désignant la première ligne qui satisfait <paramref name="isInvalid"/>, ou <c>null</c>.</summary>
    internal static DomainError? FirstLine(IReadOnlyList<JournalLine> lines, Func<JournalLine, bool> isInvalid, string code, string message)
    {
        ArgumentNullException.ThrowIfNull(lines);
        for (var index = 0; index < lines.Count; index++)
        {
            if (isInvalid(lines[index]))
            {
                return DomainError.WithDetails(
                    code,
                    message,
                    ("lineIndex", index.ToString(CultureInfo.InvariantCulture)),
                    ("account", lines[index].Account.Value));
            }
        }

        return null;
    }
}
