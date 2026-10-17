using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ledger.Command.Api.Tests.Support;

/// <summary>Émetteur de jetons de test, à la place du realm Keycloak : clé RSA propre à l'exécution.</summary>
public static class TestTokens
{
    public const string Issuer = "https://keycloak.test/realms/ledger";
    public const string Audience = "ledger-api";
    public const string Client = "caisse-1";

    private static readonly RSA _rsa = RSA.Create(2048);

    public static SecurityKey SigningKey { get; } = new RsaSecurityKey(_rsa) { KeyId = "test-key" };

    /// <summary>Jeton valide pour l'API : audience, scope <c>ledger:write</c> et client <c>caisse-1</c>.</summary>
    public static string Valid() => Create();

    public static string Create(
        string? scope = "openid ledger:write",
        string? client = Client,
        string audience = Audience,
        string issuer = Issuer,
        DateTime? expires = null,
        SecurityKey? key = null)
    {
        var claims = new List<Claim> { new("sub", Guid.NewGuid().ToString()) };
        if (scope is not null)
        {
            claims.Add(new Claim("scope", scope));
        }

        if (client is not null)
        {
            claims.Add(new Claim("azp", client));
        }

        var now = DateTime.UtcNow;
        var expiresAt = expires ?? now.AddMinutes(5);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = expiresAt < now ? expiresAt.AddMinutes(-10) : now.AddMinutes(-1),
            IssuedAt = expiresAt < now ? expiresAt.AddMinutes(-10) : now.AddMinutes(-1),
            Expires = expiresAt,
            SigningCredentials = new SigningCredentials(key ?? SigningKey, SecurityAlgorithms.RsaSha256),
        });
    }

    /// <summary>Clé que l'API ne connaît pas : un jeton signé par elle est refusé.</summary>
    public static SecurityKey ForeignKey() => new RsaSecurityKey(RSA.Create(2048)) { KeyId = "inconnue" };
}
