namespace Ledger.Authorizer.Tests;

public sealed class PolitiqueRoutesTests
{
    private const string Etape = "arn:aws:execute-api:ca-central-1:123456789012:abcdef1234/v1";

    [Fact]
    public void La_politique_ne_depend_pas_de_la_route_qui_l_a_produite()
    {
        var portees = new HashSet<string> { "ledger:write" };

        var depuisEcriture = PolitiqueRoutes.Ressources($"{Etape}/PUT/v1/journal-entries/1", portees);
        var depuisLecture = PolitiqueRoutes.Ressources($"{Etape}/GET/v1/trial-balance", portees);

        Assert.Equal(depuisEcriture, depuisLecture);
        Assert.Equal([$"{Etape}/POST/*", $"{Etape}/PUT/*"], depuisEcriture);
    }

    [Fact]
    public void Un_scope_inconnu_n_accorde_rien()
    {
        Assert.Empty(PolitiqueRoutes.Ressources($"{Etape}/GET/v1/trial-balance", new HashSet<string> { "ledger:admin" }));
    }

    [Fact]
    public void Le_refus_couvre_toute_l_etape()
    {
        Assert.Equal($"{Etape}/*/*", PolitiqueRoutes.ToutesLesRoutes($"{Etape}/GET/v1/periods/2026-11"));
    }

    [Theory]
    [InlineData("arn:aws:execute-api:ca-central-1:123456789012:abcdef1234")]
    [InlineData("arn:aws:execute-api:ca-central-1:123456789012:abcdef1234//GET/")]
    [InlineData("arn:aws:lambda:ca-central-1:123456789012:function/x/y")]
    [InlineData(" ")]
    public void Un_arn_inattendu_est_rejete(string methodArn)
    {
        Assert.ThrowsAny<ArgumentException>(() => PolitiqueRoutes.BaseEtape(methodArn));
    }
}
