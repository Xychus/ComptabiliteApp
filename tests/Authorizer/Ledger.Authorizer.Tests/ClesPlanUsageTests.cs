using System.Reflection;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace Ledger.Authorizer.Tests;

public sealed class ClesPlanUsageTests
{
    [Fact]
    public async Task La_cle_est_lue_dechiffree_sous_le_prefixe()
    {
        var ssm = SsmSimule.Creer(new Dictionary<string, string> { ["/ledger-test/plans-usage/ledger-exemple"] = "valeur" });

        var cle = await new MagasinClesPlanUsageSsm(ssm, "/ledger-test/plans-usage").TrouverAsync("ledger-exemple", TestContext.Current.CancellationToken);

        Assert.Equal("valeur", cle);
        var requete = Assert.Single(((SsmSimule)(object)ssm).Requetes);
        Assert.True(requete.WithDecryption);
    }

    [Fact]
    public async Task Un_parametre_absent_donne_null()
    {
        var ssm = SsmSimule.Creer(new Dictionary<string, string>());

        Assert.Null(await new MagasinClesPlanUsageSsm(ssm, "/ledger-test/plans-usage").TrouverAsync("ledger-exemple", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../autre/parametre")]
    [InlineData("client avec espace")]
    public async Task Un_nom_de_client_hors_du_jeu_de_caracteres_n_appelle_pas_ssm(string clientId)
    {
        var ssm = SsmSimule.Creer(new Dictionary<string, string>());

        Assert.Null(await new MagasinClesPlanUsageSsm(ssm, "/p").TrouverAsync(clientId, TestContext.Current.CancellationToken));
        Assert.Empty(((SsmSimule)(object)ssm).Requetes);
    }

    [Fact]
    public async Task Le_cache_evite_les_relectures_jusqu_a_l_expiration_y_compris_pour_une_absence()
    {
        var source = new MagasinClesEnMemoire(new Dictionary<string, string> { ["a"] = "cle-a" });
        var horloge = new HorlogeFixe(RealmDeTest.Maintenant);
        var cache = new MagasinClesPlanUsageEnCache(source, TimeSpan.FromMinutes(5), horloge);
        var annulation = TestContext.Current.CancellationToken;

        Assert.Equal("cle-a", await cache.TrouverAsync("a", annulation));
        Assert.Equal("cle-a", await cache.TrouverAsync("a", annulation));
        Assert.Null(await cache.TrouverAsync("b", annulation));
        Assert.Null(await cache.TrouverAsync("b", annulation));
        Assert.Equal(2, source.Appels);

        horloge.Maintenant += TimeSpan.FromMinutes(5);
        Assert.Equal("cle-a", await cache.TrouverAsync("a", annulation));
        Assert.Equal(3, source.Appels);
    }
}

/// <summary>Client SSM simulé : seul <c>GetParameterAsync</c> est implémenté.</summary>
#pragma warning disable CA1852 // DispatchProxy exige un type dérivable.
internal class SsmSimule : DispatchProxy
#pragma warning restore CA1852
{
    private IReadOnlyDictionary<string, string> _parametres = new Dictionary<string, string>();

    public List<GetParameterRequest> Requetes { get; } = [];

    public static IAmazonSimpleSystemsManagement Creer(IReadOnlyDictionary<string, string> parametres)
    {
        var proxy = Create<IAmazonSimpleSystemsManagement, SsmSimule>();
        ((SsmSimule)(object)proxy)._parametres = parametres;
        return proxy;
    }

    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
    {
        if (targetMethod?.Name != nameof(IAmazonSimpleSystemsManagement.GetParameterAsync) || args?[0] is not GetParameterRequest requete)
        {
            throw new NotSupportedException(targetMethod?.Name);
        }

        Requetes.Add(requete);
        return _parametres.TryGetValue(requete.Name, out var valeur)
            ? Task.FromResult(new GetParameterResponse { Parameter = new Parameter { Name = requete.Name, Value = valeur } })
            : Task.FromException<GetParameterResponse>(new ParameterNotFoundException("absent"));
    }
}
