using Ledger.SharedKernel;

namespace Ledger.Command.Domain.Taxes;

/// <summary>Niveau auquel la taxe est arrondie au cent (ADR 0008, H1).</summary>
public enum TaxRoundingLevel
{
    /// <summary>Taxe calculée sur le total taxable de la facture, puis arrondie.</summary>
    Invoice = 0,

    /// <summary>Taxe calculée et arrondie ligne par ligne, puis additionnée.</summary>
    Line = 1,
}

/// <summary>Politique d'arrondi des taxes (ADR 0008, H1 et H2).</summary>
public sealed record TaxRoundingPolicy(TaxRoundingLevel Level, MidpointRounding Mode)
{
    public static TaxRoundingPolicy Default { get; } = new(TaxRoundingLevel.Invoice, MidpointRounding.AwayFromZero);
}

/// <summary>Ventilation d'une facture : sous-total hors taxes, TPS, TVQ.</summary>
public sealed record TaxBreakdown(Money Subtotal, Money Gst, Money Qst)
{
    public Money Total => Subtotal + Gst + Qst;
}

/// <summary>Calcul des taxes de vente d'une facture.</summary>
public interface ITaxCalculator
{
    /// <param name="taxableAmounts">Montants hors taxes des lignes taxables, dans une même devise.</param>
    TaxBreakdown Calculate(IReadOnlyList<Money> taxableAmounts);
}

/// <summary>TPS (5 %) et TVQ (9,975 %) sur le même montant hors taxes (ADR 0008, H3).</summary>
public sealed class QuebecSalesTaxCalculator(TaxRoundingPolicy policy) : ITaxCalculator
{
    public const decimal GstRate = 0.05m;
    public const decimal QstRate = 0.09975m;

    public QuebecSalesTaxCalculator()
        : this(TaxRoundingPolicy.Default)
    {
    }

    public TaxBreakdown Calculate(IReadOnlyList<Money> taxableAmounts)
    {
        ArgumentNullException.ThrowIfNull(taxableAmounts);
        if (taxableAmounts.Count == 0)
        {
            throw new ArgumentException("Au moins un montant taxable est requis.", nameof(taxableAmounts));
        }

        var subtotal = taxableAmounts.Skip(1).Aggregate(taxableAmounts[0], (sum, amount) => sum + amount);
        return policy.Level == TaxRoundingLevel.Line
            ? new TaxBreakdown(subtotal, SumOfRounded(taxableAmounts, GstRate), SumOfRounded(taxableAmounts, QstRate))
            : new TaxBreakdown(subtotal, Tax(subtotal, GstRate), Tax(subtotal, QstRate));
    }

    private Money Tax(Money amount, decimal rate) => amount.MultiplyBy(rate).Round(policy.Mode);

    private Money SumOfRounded(IReadOnlyList<Money> amounts, decimal rate) =>
        amounts.Select(amount => Tax(amount, rate)).Aggregate((sum, tax) => sum + tax);
}
