// Racine de composition de l'API de commandes : aucune logique ici, voir CommandApiHosting.
using Ledger.Command.Api;

var builder = WebApplication.CreateBuilder(args);
builder.AddCommandApi();

var app = builder.Build();
app.UseCommandApi();

await app.RunAsync().ConfigureAwait(false);
