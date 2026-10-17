namespace Ledger.SharedKernel.Tests;

public sealed class CurrencyTests
{
    [Theory]
    [InlineData("CAD")]
    [InlineData("USD")]
    [InlineData("EUR")]
    public void Devise_connue(string code)
    {
        var result = Currency.FromCode(code);

        Assert.True(result.IsSuccess);
        Assert.Equal(code, result.Value.Code);
        Assert.Equal(2, result.Value.MinorUnits);
        Assert.Equal(code, result.Value.ToString());
    }

    [Theory]
    [InlineData("cad")]
    [InlineData("XYZ")]
    [InlineData("")]
    [InlineData(null)]
    public void Devise_inconnue_est_rejetee(string? code)
    {
        var result = Currency.FromCode(code!);

        Assert.True(result.IsFailure);
        Assert.Equal("UnknownCurrency", result.Error.Code);
    }
}
