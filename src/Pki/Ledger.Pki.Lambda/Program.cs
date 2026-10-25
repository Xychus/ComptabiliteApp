using Amazon.Lambda.Core;
using Amazon.Lambda.RuntimeSupport;
using Amazon.Lambda.Serialization.SystemTextJson;
using Ledger.Pki.Infrastructure;
using Ledger.Pki.Lambda;
using Microsoft.Extensions.DependencyInjection;

// Lambda de la PKI interne (ADR 0010) : rotation des certificats finaux, inventaire des échéances,
// demande de signature de la cérémonie annuelle. Client autonome de l'API d'exécution Lambda.
using var fournisseur = new ServiceCollection()
    .AjouterPkiAws()
    .AjouterPki(OptionsPki.DepuisEnvironnement(Environment.GetEnvironmentVariable))
    .BuildServiceProvider();
var gestionnaire = fournisseur.GetRequiredService<GestionnaireEvenementsPki>();

async Task<ReponsePki> TraiterAsync(EvenementPki evenement, ILambdaContext contexte)
{
    // Marge pour journaliser avant l'arrêt forcé par Lambda.
    using var annulation = new CancellationTokenSource(contexte.RemainingTime - TimeSpan.FromSeconds(5));
    var reponse = await gestionnaire.TraiterAsync(evenement, annulation.Token).ConfigureAwait(false);
    contexte.Logger.LogInformation(reponse.Message);
    return reponse;
}

await LambdaBootstrapBuilder
    .Create<EvenementPki, ReponsePki>(TraiterAsync, new SourceGeneratorLambdaJsonSerializer<ContexteJsonPki>())
    .Build()
    .RunAsync()
    .ConfigureAwait(false);
