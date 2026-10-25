using Ledger.Pki.Application.Sentinelle;
using Ledger.Pki.Infrastructure;
using Ledger.Pki.Sentinelle;
using Ledger.SharedKernel;

// Sentinelle d'un broker Kafka (ADR 0024 révisée). Deux modes, dans la même tâche ECS :
//   « installer » : conteneur d'initialisation, écrit les fichiers PEM puis s'arrête (code 0) ;
//   sans argument : surveille le secret et arrête la tâche quand un redémarrage est sûr.
var builder = Host.CreateApplicationBuilder(args.Where(a => a != "installer").ToArray());
var configuration = builder.Configuration.GetSection(OptionsSentinelle.Section).Get<ConfigurationSentinelle>() ?? new ConfigurationSentinelle();
configuration.Valider();
var options = builder.Configuration.GetSection(OptionsSentinelle.Section).Get<OptionsSentinelle>() ?? new OptionsSentinelle();

builder.Services
    .AjouterSentinelleAws(configuration.Secret, configuration.Dossier, configuration.Serveurs, configuration.NombreNoeuds)
    .AddSingleton(options)
    .AddSingleton(TimeProvider.System)
    .AddSingleton<IClock, SystemClock>()
    .AddSingleton<Ledger.Pki.Application.Sentinelle.Sentinelle>();

if (args.Contains("installer"))
{
    using var hoteInstallation = builder.Build();
    var version = await hoteInstallation.Services.GetRequiredService<Ledger.Pki.Application.Sentinelle.Sentinelle>()
        .InstallerAsync(CancellationToken.None).ConfigureAwait(false);
    Console.WriteLine($"Certificat installé : version {version}.");
    return;
}

builder.Services.AddHostedService<ServiceSentinelle>();
using var hote = builder.Build();
await hote.RunAsync().ConfigureAwait(false);
