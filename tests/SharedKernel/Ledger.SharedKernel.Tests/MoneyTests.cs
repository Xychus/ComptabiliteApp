namespace Ledger.SharedKernel.Tests;

public sealed class MoneyTests
{
    private static Money Cad(decimal amount) => new(amount, Currency.Cad);

    [Fact]
    public void Addition_et_soustraction_dans_la_meme_devise()
    {
        Assert.Equal(Cad(15.25m), Cad(10.00m) + Cad(5.25m));
        Assert.Equal(Cad(4.75m), Cad(10.00m) - Cad(5.25m));
        Assert.Equal(Cad(-3m), -Cad(3m));
    }

    [Fact]
    public void Operation_entre_devises_differentes_est_un_bogue()
    {
        var usd = new Money(1m, Currency.Usd);

        Assert.Throws<InvalidOperationException>(() => Cad(1m) + usd);
        Assert.Throws<InvalidOperationException>(() => Cad(1m) - usd);
    }

    [Fact]
    public void Egalite_par_valeur_independante_de_l_echelle()
    {
        Assert.Equal(Cad(5.0m), Cad(5.00m));
        Assert.NotEqual(Cad(5m), new Money(5m, Currency.Usd));
    }

    [Theory]
    [InlineData("9.975", MidpointRounding.AwayFromZero, "9.98")]
    [InlineData("9.965", MidpointRounding.AwayFromZero, "9.97")]
    [InlineData("9.965", MidpointRounding.ToEven, "9.96")]
    [InlineData("-9.975", MidpointRounding.AwayFromZero, "-9.98")]
    public void Arrondi_explicite_aux_unites_mineures(string amount, MidpointRounding mode, string expected)
    {
        var rounded = Cad(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)).Round(mode);

        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), rounded.Amount);
    }

    [Fact]
    public void Multiplication_par_un_taux_n_arrondit_pas()
    {
        Assert.Equal(9.975m, Cad(100m).MultiplyBy(0.09975m).Amount);
    }

    [Theory]
    [InlineData("1.23", true)]
    [InlineData("1.2", true)]
    [InlineData("1.234", false)]
    public void Detecte_les_montants_plus_fins_que_le_cent(string amount, bool expected)
    {
        Assert.Equal(expected, Cad(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture)).IsWholeMinorUnits);
    }

    [Fact]
    public void Indicateurs_de_signe()
    {
        Assert.True(Money.Zero(Currency.Cad).IsZero);
        Assert.True(Cad(0.01m).IsPositive);
        Assert.True(Cad(-0.01m).IsNegative);
    }

    [Fact]
    public void Representation_textuelle_invariante()
    {
        Assert.Equal("114.98", Cad(114.98m).ToDecimalString());
        Assert.Equal("114.98 CAD", Cad(114.98m).ToString());
    }

    [Fact]
    public void Une_devise_est_obligatoire() =>
        Assert.Throws<ArgumentNullException>(() => new Money(1m, null!));
}
