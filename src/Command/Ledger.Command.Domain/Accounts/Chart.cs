using System.Collections.Immutable;
using Ledger.Command.Domain.Entries;
using Ledger.SharedKernel;

namespace Ledger.Command.Domain.Accounts;

/// <summary>Définit ou modifie un compte du plan comptable d'une entité (ADR 0012).</summary>
public sealed record DefineAccount(
    EntityId Entity,
    AccountId Account,
    string Name,
    Currency Currency,
    Side NormalSide,
    bool IsActive,
    bool NoNegativeBalance);

/// <summary>État complet d'un compte après sa définition ou sa modification, dans le flux <c>chart-{entité}</c>.</summary>
public sealed record AccountDefined(
    EntityId Entity,
    AccountId Account,
    string Name,
    Currency Currency,
    Side NormalSide,
    bool IsActive,
    bool NoNegativeBalance) : IDomainEvent
{
    public AccountInfo ToInfo() => new(Account, Currency, NormalSide, IsActive, NoNegativeBalance);
}

/// <summary>Plan comptable d'une entité, reconstruit depuis son flux d'événements.</summary>
public sealed record ChartState(EntityId Entity, ImmutableDictionary<AccountId, AccountDefined> Accounts) : IChartOfAccounts
{
    public static ChartState Initial(EntityId entity) => new(entity, ImmutableDictionary<AccountId, AccountDefined>.Empty);

    public AccountInfo? Find(AccountId account) => Accounts.GetValueOrDefault(account)?.ToInfo();

    public ChartState Evolve(IDomainEvent @event) => @event switch
    {
        AccountDefined defined when defined.Entity == Entity => this with { Accounts = Accounts.SetItem(defined.Account, defined) },
        _ => this,
    };
}

/// <summary>Décisions sur le plan comptable : création, renommage, activation (ADR 0012).</summary>
public static class ChartDecider
{
    public const int MaxNameLength = 200;

    /// <summary>
    /// Décide la définition d'un compte. Une définition identique à l'état courant ne produit
    /// aucun événement ; la devise, le sens normal et la contrainte de solde sont immuables.
    /// </summary>
    public static Result<IReadOnlyList<IDomainEvent>> Decide(ChartState state, DefineAccount command)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        if (state.Entity != command.Entity)
        {
            throw new ArgumentException($"Plan comptable de {state.Entity} fourni pour {command.Entity}.", nameof(command));
        }

        if (string.IsNullOrWhiteSpace(command.Name) || command.Name.Length > MaxNameLength)
        {
            return Reject(AccountErrors.InvalidName(command.Account));
        }

        var defined = new AccountDefined(command.Entity, command.Account, command.Name, command.Currency, command.NormalSide, command.IsActive, command.NoNegativeBalance);
        if (state.Accounts.GetValueOrDefault(command.Account) is not { } existing)
        {
            return Events.Of(defined);
        }

        if (ImmutableAttributeChanged(existing, defined) is { } attribute)
        {
            return Reject(AccountErrors.AttributeImmutable(command.Account, attribute));
        }

        return existing == defined ? Events.Of() : Events.Of(defined);
    }

    private static string? ImmutableAttributeChanged(AccountDefined existing, AccountDefined candidate) =>
        existing.Currency != candidate.Currency ? "currency"
        : existing.NormalSide != candidate.NormalSide ? "normalSide"
        : existing.NoNegativeBalance != candidate.NoNegativeBalance ? "noNegativeBalance"
        : null;

    private static Result<IReadOnlyList<IDomainEvent>> Reject(DomainError error) => Result.Failure<IReadOnlyList<IDomainEvent>>(error);
}

public static class AccountErrors
{
    public const string InvalidNameCode = "AccountInvalidName";
    public const string AttributeImmutableCode = "AccountAttributeImmutable";

    public static DomainError InvalidName(AccountId account) =>
        DomainError.WithDetails(InvalidNameCode, $"Le nom du compte {account} est obligatoire ({ChartDecider.MaxNameLength} caractères au plus).", ("account", account.Value));

    public static DomainError AttributeImmutable(AccountId account, string attribute) =>
        DomainError.WithDetails(
            AttributeImmutableCode,
            $"L'attribut {attribute} du compte {account} ne peut plus changer : il conditionne les écritures déjà passées.",
            ("account", account.Value),
            ("attribute", attribute));
}
