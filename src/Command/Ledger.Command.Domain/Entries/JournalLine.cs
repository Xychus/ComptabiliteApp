using Ledger.SharedKernel;

namespace Ledger.Command.Domain.Entries;

/// <summary>Sens d'une ligne d'écriture.</summary>
public enum Side
{
    Debit = 0,
    Credit = 1,
}

/// <summary>Ligne d'écriture : un compte, un sens, un montant positif.</summary>
public sealed record JournalLine(AccountId Account, Side Side, Money Amount)
{
    /// <summary>Même ligne dans le sens opposé (utilisée par la contre-passation).</summary>
    public JournalLine Inverted() => this with { Side = Side == Side.Debit ? Side.Credit : Side.Debit };
}
