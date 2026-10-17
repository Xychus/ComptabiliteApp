namespace Ledger.SharedKernel.Tests;

public sealed class ResultTests
{
    private static readonly DomainError _error = new("Boom", "Échec de test");

    [Fact]
    public void Succes_porte_une_valeur()
    {
        var result = Result.Success(42);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal(42, result.Value);
        Assert.Throws<InvalidOperationException>(() => result.Error);
        Assert.Equal("Succès(42)", result.ToString());
    }

    [Fact]
    public void Echec_porte_une_erreur()
    {
        var result = Result.Failure<int>(_error);

        Assert.True(result.IsFailure);
        Assert.Equal(_error, result.Error);
        Assert.Throws<InvalidOperationException>(() => result.Value);
        Assert.Equal("Échec(Boom)", result.ToString());
    }

    [Fact]
    public void Map_et_Bind_propagent_le_succes()
    {
        var result = Result.Success(2).Map(x => x * 10).Bind(x => Result.Success(x + 1));

        Assert.Equal(21, result.Value);
    }

    [Fact]
    public void Map_et_Bind_propagent_l_echec_sans_executer()
    {
        var executed = false;
        var result = Result.Failure<int>(_error)
            .Map(x => { executed = true; return x; })
            .Bind(x => { executed = true; return Result.Success(x); });

        Assert.False(executed);
        Assert.Equal("Boom", result.Error.Code);
    }

    [Fact]
    public void Erreur_avec_details()
    {
        var error = DomainError.WithDetails("EntryNotBalanced", "Écriture déséquilibrée", ("currency", "CAD"));

        Assert.Equal("CAD", error.Details!["currency"]);
    }
}
