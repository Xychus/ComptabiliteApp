using System.Globalization;
using Ledger.Command.Domain.Accounts;
using Ledger.Command.Domain.Entries;
using Ledger.SharedKernel;

namespace Ledger.Command.Domain.ConstrainedAccounts;

/// <summary>Applique une ligne d'écriture à un compte contraint.</summary>
public sealed record ApplyAccountLine(EntryId SourceEntryId, JournalLine Line);

/// <summary>
/// Ligne appliquée à un compte contraint, stockée dans le flux <c>account-{entité}-{compte}</c>.
/// <paramref name="BalanceAfter"/> permet de vérifier l'invariant sans lire de projection.
/// </summary>
public sealed record AccountLinePosted(
    EntityId Entity,
    AccountId Account,
    EntryId SourceEntryId,
    Side Side,
    Money Amount,
    Money BalanceAfter) : IDomainEvent;

/// <summary>État d'un compte contraint : solde courant (sens normal) et version du flux.</summary>
public sealed record ConstrainedAccountState(EntityId Entity, AccountInfo Account, Money Balance, long Version)
{
    public static ConstrainedAccountState Initial(EntityId entity, AccountInfo account)
    {
        ArgumentNullException.ThrowIfNull(account);
        return new(entity, account, Money.Zero(account.Currency), 0);
    }

    public ConstrainedAccountState Evolve(IDomainEvent @event) => @event switch
    {
        AccountLinePosted posted => this with { Balance = posted.BalanceAfter, Version = Version + 1 },
        _ => this,
    };
}

/// <summary>Décision « solde non négatif » d'un compte contraint (ADR 0008, H7).</summary>
public static class ConstrainedAccountDecider
{
    public const string InsufficientBalanceCode = "InsufficientBalance";

    public static Result<IReadOnlyList<IDomainEvent>> Decide(ConstrainedAccountState state, ApplyAccountLine command)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        var account = state.Account;
        var line = command.Line;
        if (!account.NoNegativeBalance)
        {
            throw new ArgumentException($"Le compte {account.Id} n'est pas contraint.", nameof(state));
        }

        if (line.Account != account.Id)
        {
            throw new ArgumentException($"Ligne du compte {line.Account} appliquée au compte {account.Id}.", nameof(command));
        }

        var delta = line.Side == account.NormalSide ? line.Amount : -line.Amount;
        var balanceAfter = state.Balance + delta;
        if (balanceAfter.IsNegative)
        {
            return Result.Failure<IReadOnlyList<IDomainEvent>>(DomainError.WithDetails(
                InsufficientBalanceCode,
                $"Solde insuffisant sur le compte {account.Id} : {state.Balance}, mouvement {delta}.",
                ("account", account.Id.Value),
                ("balance", state.Balance.ToDecimalString()),
                ("delta", delta.ToDecimalString()),
                ("version", state.Version.ToString(CultureInfo.InvariantCulture))));
        }

        return Events.Of(new AccountLinePosted(state.Entity, account.Id, command.SourceEntryId, line.Side, line.Amount, balanceAfter));
    }
}
