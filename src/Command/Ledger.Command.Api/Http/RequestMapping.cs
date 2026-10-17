using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;
using Ledger.Command.Application.Periods;
using Ledger.Command.Domain.Accounts;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Periods;
using Ledger.Contracts.Http.V1;
using Ledger.SharedKernel;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ledger.Command.Api.Http;

/// <summary>Erreurs de forme d'une requête, par champ (chemin JSON en camelCase).</summary>
public sealed class RequestErrors
{
    private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

    public bool Any => _errors.Count > 0;

    public void Add(string field, string message)
    {
        if (!_errors.TryGetValue(field, out var messages))
        {
            messages = [];
            _errors.Add(field, messages);
        }

        messages.Add(message);
    }

    public ValidationProblem ToProblem() =>
        TypedResults.ValidationProblem(
            _errors.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal),
            title: "Requête invalide",
            type: DomainProblems.TypePrefix + "InvalidRequest");
}

/// <summary>
/// Traduction des contrats HTTP en commandes de l'Application : vérifications de forme seulement
/// (identifiants, formats, valeurs énumérées). Les règles métier restent dans le domaine (<c>422</c>).
/// </summary>
public static partial class RequestMapping
{
    public const int MaxEntityIdLength = 64;
    public const int MaxAccountIdLength = 32;
    public const int MaxDescriptionLength = 500;

    public static bool TryMap(Guid entryId, PostJournalEntryRequest request, RequestErrors errors, [NotNullWhen(true)] out PostEntry? command)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(errors);
        var id = EntryIdOf(entryId, "entryId", errors);
        var entity = EntityOf(request.EntityId, errors);
        DescriptionOf(request.Description, errors);
        var lines = new List<JournalLine>(request.Lines.Count);
        for (var index = 0; index < request.Lines.Count; index++)
        {
            if (LineOf(request.Lines[index], $"lines[{index.ToString(CultureInfo.InvariantCulture)}]", errors) is { } line)
            {
                lines.Add(line);
            }
        }

        command = errors.Any ? null : new PostEntry(id!.Value, entity!.Value, request.BusinessDate, request.Description, EquatableList.Of(lines));
        return command is not null;
    }

    public static bool TryMap(Guid entryId, ReverseJournalEntryRequest request, RequestErrors errors, [NotNullWhen(true)] out ReverseEntry? command)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(errors);
        var original = EntryIdOf(entryId, "entryId", errors);
        var reversal = EntryIdOf(request.ReversalEntryId, "reversalEntryId", errors);
        DescriptionOf(request.Description, errors);
        command = errors.Any ? null : new ReverseEntry(original!.Value, reversal!.Value, request.BusinessDate, request.Description);
        return command is not null;
    }

    public static bool TryMap(string period, PeriodCommandRequest request, RequestErrors errors, [NotNullWhen(true)] out PeriodCommand? command)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(errors);
        var periodId = PeriodId.Parse(period);
        if (periodId.IsFailure)
        {
            errors.Add("period", periodId.Error.Message);
        }

        var entity = EntityOf(request.EntityId, errors);
        if (request.ExpectedVersion < 0)
        {
            errors.Add("expectedVersion", "La version attendue est un entier positif ou nul.");
        }

        command = errors.Any ? null : new PeriodCommand(entity!.Value, periodId.Value, request.ExpectedVersion);
        return command is not null;
    }

    public static bool TryMap(string accountId, DefineAccountRequest request, RequestErrors errors, [NotNullWhen(true)] out DefineAccount? command)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(errors);
        var account = AccountOf(accountId, "accountId", errors);
        var entity = EntityOf(request.EntityId, errors);
        var currency = CurrencyOf(request.Currency, "currency", errors);
        var normalSide = SideOf(request.NormalSide, "normalSide", errors);
        command = errors.Any
            ? null
            : new DefineAccount(entity!.Value, account!.Value, request.Name, currency!, normalSide!.Value, request.IsActive, request.NoNegativeBalance);
        return command is not null;
    }

    private static JournalLine? LineOf(JournalLineDto line, string field, RequestErrors errors)
    {
        var account = AccountOf(line.AccountId, field + ".accountId", errors);
        var side = SideOf(line.Side, field + ".side", errors);
        var amount = MoneyOf(line.Amount, field + ".amount", errors);
        return account is { } a && side is { } s && amount is { } m ? new JournalLine(a, s, m) : null;
    }

    private static EntryId? EntryIdOf(Guid value, string field, RequestErrors errors)
    {
        if (EntryId.TryCreate(value, out var entryId))
        {
            return entryId;
        }

        errors.Add(field, "Un identifiant d'écriture est un UUIDv7.");
        return null;
    }

    private static EntityId? EntityOf(string value, RequestErrors errors)
    {
        if (Identifier().IsMatch(value) && value.Length <= MaxEntityIdLength)
        {
            return new EntityId(value);
        }

        errors.Add("entityId", $"Identifiant d'entité attendu : 1 à {MaxEntityIdLength} caractères parmi lettres, chiffres, « . », « _ » et « - ».");
        return null;
    }

    private static AccountId? AccountOf(string value, string field, RequestErrors errors)
    {
        if (Identifier().IsMatch(value) && value.Length <= MaxAccountIdLength)
        {
            return new AccountId(value);
        }

        errors.Add(field, $"Code de compte attendu : 1 à {MaxAccountIdLength} caractères parmi lettres, chiffres, « . », « _ » et « - ».");
        return null;
    }

    private static Side? SideOf(string value, string field, RequestErrors errors)
    {
        switch (value)
        {
            case nameof(Side.Debit):
                return Side.Debit;
            case nameof(Side.Credit):
                return Side.Credit;
            default:
                errors.Add(field, "Sens attendu : « Debit » ou « Credit ».");
                return null;
        }
    }

    private static Currency? CurrencyOf(string value, string field, RequestErrors errors)
    {
        var currency = Currency.FromCode(value);
        if (currency.IsSuccess)
        {
            return currency.Value;
        }

        errors.Add(field, currency.Error.Message);
        return null;
    }

    private static Money? MoneyOf(MoneyDto money, string field, RequestErrors errors)
    {
        var currency = CurrencyOf(money.Currency, field + ".currency", errors);
        if (!DecimalAmount().IsMatch(money.Amount))
        {
            errors.Add(field + ".amount", "Montant attendu en chaîne décimale (ex. « 114.98 »), au plus 18 chiffres avant la virgule.");
            return null;
        }

        return currency is null ? null : new Money(decimal.Parse(money.Amount, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture), currency);
    }

    private static void DescriptionOf(string? description, RequestErrors errors)
    {
        if (description is { Length: > MaxDescriptionLength })
        {
            errors.Add("description", $"Libellé limité à {MaxDescriptionLength} caractères.");
        }
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    /// <summary>Le signe et le nombre de décimales sont laissés au domaine, qui désigne la ligne fautive (<c>422</c>).</summary>
    [GeneratedRegex(@"^-?[0-9]{1,18}(\.[0-9]{1,10})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex DecimalAmount();
}
