namespace Ledger.Authorizer.Tests;

public sealed class AutorizerTests
{
    private const string MethodArn = "arn:aws:execute-api:ca-central-1:123456789012:abcdef1234/v1/PUT/v1/journal-entries/0192";
    private const string Etape = "arn:aws:execute-api:ca-central-1:123456789012:abcdef1234/v1";

    private readonly RealmDeTest _realm = new();
    private readonly MagasinClesEnMemoire _cles = new(new Dictionary<string, string> { ["ledger-exemple"] = "cle-plan-exemple" });

    private Autorizer Autorizer() => new(_realm.Validateur(), _cles);

    private static EvenementAutorisation Evenement(string? autorisation, string nomEntete = "Authorization") => new()
    {
        MethodArn = MethodArn,
        Headers = autorisation is null ? [] : new Dictionary<string, string> { [nomEntete] = autorisation },
    };

    [Fact]
    public async Task Un_client_lecture_ecriture_obtient_toutes_les_routes_de_ses_scopes()
    {
        var reponse = await Autorizer().AutoriserAsync(Evenement($"Bearer {_realm.Jeton()}"), TestContext.Current.CancellationToken);

        Assert.Equal("ledger-exemple", reponse.PrincipalId);
        var declaration = Assert.Single(reponse.PolicyDocument.Statement);
        Assert.Equal("Allow", declaration.Effect);
        Assert.Equal("execute-api:Invoke", declaration.Action);
        Assert.Equal([$"{Etape}/GET/*", $"{Etape}/HEAD/*", $"{Etape}/POST/*", $"{Etape}/PUT/*"], declaration.Resource);
        Assert.Equal("cle-plan-exemple", reponse.UsageIdentifierKey);
        Assert.Equal("ledger-exemple", reponse.Context!["clientId"]);
    }

    [Fact]
    public async Task Un_client_en_lecture_seule_n_obtient_aucune_route_d_ecriture()
    {
        var jeton = _realm.Jeton(scope: "ledger:read");

        var reponse = await Autorizer().AutoriserAsync(Evenement($"Bearer {jeton}"), TestContext.Current.CancellationToken);

        var declaration = Assert.Single(reponse.PolicyDocument.Statement);
        Assert.Equal("Allow", declaration.Effect);
        Assert.Equal([$"{Etape}/GET/*", $"{Etape}/HEAD/*"], declaration.Resource);
        Assert.DoesNotContain(declaration.Resource, r => r.Contains("/PUT/", StringComparison.Ordinal) || r.Contains("/POST/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Un_jeton_sans_scope_du_grand_livre_recoit_un_refus_sur_toute_l_etape()
    {
        var reponse = await Autorizer().AutoriserAsync(Evenement($"Bearer {_realm.Jeton(scope: "profile email")}"), TestContext.Current.CancellationToken);

        var declaration = Assert.Single(reponse.PolicyDocument.Statement);
        Assert.Equal("Deny", declaration.Effect);
        Assert.Equal([$"{Etape}/*/*"], declaration.Resource);
        Assert.Null(reponse.UsageIdentifierKey);
        Assert.Equal(0, _cles.Appels);
    }

    [Fact]
    public async Task Un_client_sans_plan_d_usage_recoit_un_refus()
    {
        var reponse = await Autorizer().AutoriserAsync(Evenement($"Bearer {_realm.Jeton(azp: "client-inconnu")}"), TestContext.Current.CancellationToken);

        Assert.Equal("Deny", Assert.Single(reponse.PolicyDocument.Statement).Effect);
        Assert.Equal("client-inconnu", reponse.PrincipalId);
        Assert.Null(reponse.UsageIdentifierKey);
        Assert.Null(reponse.Context);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Bearer ")]
    [InlineData("Basic dXRpbGlzYXRldXI6bW90")]
    public async Task Sans_jeton_la_reponse_est_401(string? autorisation)
    {
        var refus = await Assert.ThrowsAsync<NonAutoriseException>(() => Autorizer().AutoriserAsync(Evenement(autorisation), TestContext.Current.CancellationToken));

        Assert.Equal("Unauthorized", refus.Message);
    }

    [Fact]
    public async Task Un_jeton_d_une_mauvaise_audience_donne_401()
    {
        var jeton = _realm.Jeton(audience: "autre-api");

        var refus = await Assert.ThrowsAsync<NonAutoriseException>(() => Autorizer().AutoriserAsync(Evenement($"Bearer {jeton}"), TestContext.Current.CancellationToken));

        Assert.Equal("Unauthorized", refus.Message);
        Assert.Equal("Jeton refusé.", refus.InnerException!.Message);
    }

    [Fact]
    public async Task Le_nom_de_l_entete_et_le_schema_sont_insensibles_a_la_casse()
    {
        var reponse = await Autorizer().AutoriserAsync(Evenement($"bearer {_realm.Jeton()}", "authorization"), TestContext.Current.CancellationToken);

        Assert.Equal("Allow", Assert.Single(reponse.PolicyDocument.Statement).Effect);
    }

    [Fact]
    public async Task Un_evenement_sans_arn_de_methode_est_une_erreur()
    {
        var evenement = new EvenementAutorisation { Headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {_realm.Jeton()}" } };

        await Assert.ThrowsAsync<ArgumentException>(() => Autorizer().AutoriserAsync(evenement, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Les_constructeurs_de_refus_gardent_le_message_exige_par_api_gateway()
    {
        Assert.Equal("Unauthorized", new NonAutoriseException().Message);
        Assert.Equal("Unauthorized", new NonAutoriseException("raison", new InvalidOperationException()).Message);
    }
}
