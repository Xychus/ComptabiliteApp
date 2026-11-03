using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Amazon.SimpleSystemsManagement;
using Ledger.Authorizer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

// Autorizer Lambda de l'API privée (ADR 0031), runtime géré dotnet10. Les objets construits ici
// vivent autant que l'instance Lambda : configuration OIDC et clés de plan d'usage restent en cache.
var options = OptionsAutorizer.DepuisEnvironnement(Environment.GetEnvironmentVariable);

#pragma warning disable CA2000 // Possédés par l'instance Lambda pour toute sa durée de vie.
var canalArriere = new HttpClient(ConfianceRacinePki.CreerGestionnaire(options.CertificatRacinePki)) { Timeout = TimeSpan.FromSeconds(5) };
var ssm = new AmazonSimpleSystemsManagementClient();
#pragma warning restore CA2000

var configurationOidc = new ConfigurationManager<OpenIdConnectConfiguration>(
    $"{options.Emetteur.TrimEnd('/')}/.well-known/openid-configuration",
    new OpenIdConnectConfigurationRetriever(),
    new HttpDocumentRetriever(canalArriere) { RequireHttps = true });

var autorizer = new Autorizer(
    new ValidateurJetons(configurationOidc, options, TimeProvider.System),
    new MagasinClesPlanUsageEnCache(new MagasinClesPlanUsageSsm(ssm, options.PrefixeClesPlanUsage), options.DureeCacheCles, TimeProvider.System));

async Task<ReponseAutorisation> TraiterAsync(EvenementAutorisation evenement, ILambdaContext contexte)
{
    using var annulation = new CancellationTokenSource(contexte.RemainingTime - TimeSpan.FromSeconds(1));
    try
    {
        var reponse = await autorizer.AutoriserAsync(evenement, annulation.Token).ConfigureAwait(false);
        contexte.Logger.LogInformation($"{reponse.PolicyDocument.Statement[0].Effect} {reponse.PrincipalId}");
        return reponse;
    }
    catch (NonAutoriseException refus)
    {
        // Raison journalisée, jamais renvoyée : le message vers API Gateway doit rester « Unauthorized ».
        contexte.Logger.LogInformation($"Refus : {refus.InnerException?.Message}");
        throw;
    }
}

await LambdaBootstrapBuilder
    .Create<EvenementAutorisation, ReponseAutorisation>(TraiterAsync, new SourceGeneratorLambdaJsonSerializer<ContexteJsonAutorisation>())
    .Build()
    .RunAsync()
    .ConfigureAwait(false);
