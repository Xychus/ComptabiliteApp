// Racine de composition du worker de projections : aucune logique ici, voir ProjectionWorkerHosting.
//   (sans argument)          consommation continue de Kafka
//   rebuild <projection>     reconstruction d'une projection depuis ledger.events, puis arrêt
using Ledger.Projections.Worker;

var rebuild = args.Length == 2 && args[0] == ProjectionWorkerHosting.RebuildCommand;
var builder = Host.CreateApplicationBuilder(rebuild ? [] : args);
builder.AddProjectionWorker(consume: !rebuild);

using var host = builder.Build();
if (rebuild)
{
    return await ProjectionWorkerHosting.RunRebuildAsync(host, args[1], CancellationToken.None).ConfigureAwait(false);
}

await host.RunAsync().ConfigureAwait(false);
return 0;
