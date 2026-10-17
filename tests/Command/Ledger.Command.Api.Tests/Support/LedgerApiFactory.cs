using System.Net.Http.Headers;
using Ledger.Command.Api.Composition;
using Ledger.Command.Api.Security;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Ledger.Command.Api.Tests.Support;

/// <summary>
/// API de commandes hébergée en mémoire. Les jetons sont validés contre la clé de
/// <see cref="TestTokens"/> (configuration OIDC fournie, aucune découverte réseau) ; l'event store
/// est en mémoire, ou PostgreSQL si une chaîne de connexion est fournie.
/// </summary>
public sealed class LedgerApiFactory(string? connectionString = null, string environment = "Development", IReadOnlyDictionary<string, string?>? settings = null)
    : WebApplicationFactory<Program>
{
    public HttpClient CreateAuthenticatedClient(string? token = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token ?? TestTokens.Valid());
        return client;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment(environment);
        builder.UseSetting(LedgerAuthenticationOptions.Section + ":Authority", TestTokens.Issuer);
        if (connectionString is null)
        {
            builder.UseSetting(CommandServices.ProviderKey, CommandServices.InMemoryProvider);
        }
        else
        {
            builder.UseSetting("ConnectionStrings:" + CommandServices.ConnectionStringName, connectionString);
        }

        foreach (var (key, value) in settings ?? new Dictionary<string, string?>())
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureTestServices(services =>
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, jwt =>
            {
                // Remplace la découverte OIDC (créée par la post-configuration de JwtBearer) par une configuration fixe.
                var configuration = new OpenIdConnectConfiguration { Issuer = TestTokens.Issuer };
                configuration.SigningKeys.Add(TestTokens.SigningKey);
                jwt.Configuration = configuration;
                jwt.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            }));
    }
}
