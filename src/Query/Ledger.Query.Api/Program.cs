// Racine de composition de l'API de requête : aucune logique ici, voir HebergementApiRequete.
using Ledger.Query.Api;

var builder = WebApplication.CreateBuilder(args);
builder.AjouterApiRequete();

var app = builder.Build();
app.UtiliserApiRequete();

await app.RunAsync().ConfigureAwait(false);
