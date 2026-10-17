namespace Ledger.SharedKernel;

/// <summary>Devise ISO 4217 avec son nombre de décimales (unités mineures).</summary>
public sealed record Currency
{
    public static readonly Currency Cad = new("CAD", 2);
    public static readonly Currency Usd = new("USD", 2);
    public static readonly Currency Eur = new("EUR", 2);

    private static readonly Dictionary<string, Currency> _known =
        new[] { Cad, Usd, Eur }.ToDictionary(c => c.Code, StringComparer.Ordinal);

    private Currency(string code, int minorUnits)
    {
        Code = code;
        MinorUnits = minorUnits;
    }

    /// <summary>Code ISO 4217 en majuscules (ex. <c>CAD</c>).</summary>
    public string Code { get; }

    /// <summary>Nombre de décimales de la devise (2 pour le dollar canadien).</summary>
    public int MinorUnits { get; }

    public static Result<Currency> FromCode(string code) =>
        code is not null && _known.TryGetValue(code, out var currency)
            ? Result.Success(currency)
            : Result.Failure<Currency>(new DomainError("UnknownCurrency", $"Devise inconnue : « {code} »."));

    public override string ToString() => Code;
}
