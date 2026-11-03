using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Ledger.Authorizer.Tests;

internal sealed class HorlogeFixe(DateTimeOffset maintenant) : TimeProvider
{
    public DateTimeOffset Maintenant { get; set; } = maintenant;

    public override DateTimeOffset GetUtcNow() => Maintenant;
}

/// <summary>Realm de test : une clé ES256 publiée dans un JWKS, et un émetteur de jetons.</summary>
internal sealed class RealmDeTest : IDisposable
{
    public const string Emetteur = "https://keycloak.ledger-test.internal/realms/ledger";
    public static readonly DateTimeOffset Maintenant = new(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);

    public RealmDeTest(string kid = "cle-1")
    {
        _ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Cle = new ECDsaSecurityKey(_ecdsa) { KeyId = kid };
    }

    private readonly ECDsa _ecdsa;

    public ECDsaSecurityKey Cle { get; }

    public void Dispose() => _ecdsa.Dispose();

    public OpenIdConnectConfiguration Configuration()
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = Emetteur };
        configuration.SigningKeys.Add(Cle);
        return configuration;
    }

    public string Jeton(
        string? azp = "ledger-exemple",
        string? scope = "ledger:read ledger:write",
        string audience = "ledger-api",
        string emetteur = Emetteur,
        DateTimeOffset? emisLe = null,
        TimeSpan? duree = null,
        SigningCredentials? signature = null)
    {
        var emission = emisLe ?? Maintenant.AddMinutes(-1);
        var revendications = new Dictionary<string, object>(StringComparer.Ordinal);
        if (azp is not null)
        {
            revendications["azp"] = azp;
        }

        if (scope is not null)
        {
            revendications["scope"] = scope;
        }

        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = emetteur,
            Audience = audience,
            IssuedAt = emission.UtcDateTime,
            NotBefore = emission.UtcDateTime,
            Expires = (emission + (duree ?? TimeSpan.FromMinutes(5))).UtcDateTime,
            Claims = revendications,
            Subject = new ClaimsIdentity(),
            SigningCredentials = signature ?? new SigningCredentials(Cle, SecurityAlgorithms.EcdsaSha256),
        });
    }

    public ValidateurJetons Validateur(IConfigurationManager<OpenIdConnectConfiguration>? configuration = null, HorlogeFixe? horloge = null) =>
        new(
            configuration ?? new StaticConfigurationManager<OpenIdConnectConfiguration>(Configuration()),
            new OptionsAutorizer { Emetteur = Emetteur, PrefixeClesPlanUsage = "/ledger-test/plans-usage" },
            horloge ?? new HorlogeFixe(Maintenant));
}

/// <summary>Configuration OIDC qui ne publie les clés suivantes qu'après une demande de relecture.</summary>
internal sealed class ConfigurationAvecRotation(OpenIdConnectConfiguration avant, OpenIdConnectConfiguration apres)
    : IConfigurationManager<OpenIdConnectConfiguration>
{
    public int Relectures { get; private set; }

    public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
        Task.FromResult(Relectures == 0 ? avant : apres);

    public void RequestRefresh() => Relectures++;
}

internal sealed class MagasinClesEnMemoire(IReadOnlyDictionary<string, string> cles) : IMagasinClesPlanUsage
{
    public int Appels { get; private set; }

    public Task<string?> TrouverAsync(string clientId, CancellationToken annulation)
    {
        Appels++;
        return Task.FromResult(cles.GetValueOrDefault(clientId));
    }
}
