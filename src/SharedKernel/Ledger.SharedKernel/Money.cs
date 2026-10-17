using System.Globalization;

namespace Ledger.SharedKernel;

/// <summary>
/// Montant monétaire : <see cref="decimal"/> toujours accompagné de sa devise.
/// Les opérations entre devises différentes sont un bogue de programmation (exception) :
/// la validation métier les refuse avant tout calcul. L'arrondi est toujours explicite.
/// </summary>
public sealed record Money
{
    public Money(decimal amount, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        Amount = amount;
        Currency = currency;
    }

    public decimal Amount { get; }

    public Currency Currency { get; }

    public bool IsZero => Amount == 0m;

    public bool IsPositive => Amount > 0m;

    public bool IsNegative => Amount < 0m;

    /// <summary>Vrai si le montant ne comporte pas plus de décimales que la devise n'en autorise.</summary>
    public bool IsWholeMinorUnits => decimal.Round(Amount, Currency.MinorUnits) == Amount;

    public static Money Zero(Currency currency) => new(0m, currency);

    public static Money operator +(Money left, Money right) => left.Add(right);

    public static Money operator -(Money left, Money right) => left.Subtract(right);

    public static Money operator -(Money money) => money.Negate();

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    public Money Negate() => new(-Amount, Currency);

    /// <summary>Multiplie par un taux sans arrondir ; appeler <see cref="Round"/> ensuite.</summary>
    public Money MultiplyBy(decimal rate) => new(Amount * rate, Currency);

    /// <summary>Arrondit aux unités mineures de la devise selon le mode explicitement choisi.</summary>
    public Money Round(MidpointRounding mode) => new(decimal.Round(Amount, Currency.MinorUnits, mode), Currency);

    /// <summary>Montant formaté en chaîne décimale invariante (ex. <c>114.98</c>), sans arrondi.</summary>
    public string ToDecimalString() => Amount.ToString(CultureInfo.InvariantCulture);

    public override string ToString() => $"{ToDecimalString()} {Currency.Code}";

    private void EnsureSameCurrency(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (other.Currency != Currency)
        {
            throw new InvalidOperationException($"Devises différentes : {Currency} et {other.Currency}.");
        }
    }
}
