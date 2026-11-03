namespace Ledger.Authorizer.Tests;

public sealed class OptionsAutorizerTests
{
    private static Func<string, string?> Environnement(Dictionary<string, string> valeurs) => nom => valeurs.GetValueOrDefault(nom);

    [Fact]
    public void Les_options_sont_lues_dans_l_environnement()
    {
        var options = OptionsAutorizer.DepuisEnvironnement(Environnement(new()
        {
            ["LEDGER_EMETTEUR"] = RealmDeTest.Emetteur,
            ["LEDGER_PREFIXE_CLES_PLAN_USAGE"] = "/ledger-dev/plans-usage/",
            ["LEDGER_RACINE_PKI"] = "-----BEGIN CERTIFICATE-----",
        }));

        Assert.Equal(RealmDeTest.Emetteur, options.Emetteur);
        Assert.Equal("ledger-api", options.Audience);
        Assert.Equal("/ledger-dev/plans-usage", options.PrefixeClesPlanUsage);
        Assert.Equal("-----BEGIN CERTIFICATE-----", options.CertificatRacinePki);
    }

    [Fact]
    public void L_audience_et_l_absence_de_racine_sont_respectees()
    {
        var options = OptionsAutorizer.DepuisEnvironnement(Environnement(new()
        {
            ["LEDGER_EMETTEUR"] = RealmDeTest.Emetteur,
            ["LEDGER_PREFIXE_CLES_PLAN_USAGE"] = "/p",
            ["LEDGER_AUDIENCE"] = "autre",
        }));

        Assert.Equal("autre", options.Audience);
        Assert.Null(options.CertificatRacinePki);
    }

    [Theory]
    [InlineData(null, "/p")]
    [InlineData("http://keycloak/realms/ledger", "/p")]
    [InlineData("pas une url", "/p")]
    [InlineData(RealmDeTest.Emetteur, null)]
    public void Une_option_obligatoire_absente_ou_invalide_arrete_le_demarrage(string? emetteur, string? prefixe)
    {
        var valeurs = new Dictionary<string, string>();
        if (emetteur is not null)
        {
            valeurs["LEDGER_EMETTEUR"] = emetteur;
        }

        if (prefixe is not null)
        {
            valeurs["LEDGER_PREFIXE_CLES_PLAN_USAGE"] = prefixe;
        }

        Assert.Throws<InvalidOperationException>(() => OptionsAutorizer.DepuisEnvironnement(Environnement(valeurs)));
    }
}
