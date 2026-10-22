using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging;

namespace Ledger.EndToEnd.Tests.Support;

/// <summary>
/// Une des deux API hébergée en mémoire avec sa composition réelle : PostgreSQL, et jetons
/// validés par découverte OIDC auprès du Keycloak de test (HTTP toléré en <c>Development</c>).
/// </summary>
public sealed class FabriqueApi<TPointEntree>(IReadOnlyDictionary<string, string?> reglages) : WebApplicationFactory<TPointEntree>
    where TPointEntree : class
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        foreach (var (cle, valeur) in reglages)
        {
            builder.UseSetting(cle, valeur);
        }

        builder.ConfigureLogging(journal => journal.ClearProviders());
    }
}
