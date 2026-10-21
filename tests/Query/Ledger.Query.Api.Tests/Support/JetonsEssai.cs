using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ledger.Query.Api.Tests.Support;

/// <summary>Émetteur de jetons de test, à la place du realm Keycloak : clé RSA propre à l'exécution.</summary>
public static class JetonsEssai
{
    public const string Emetteur = "https://keycloak.test/realms/ledger";
    public const string Audience = "ledger-api";

    private static readonly RSA _rsa = RSA.Create(2048);
    private static readonly RSA _rsaEtrangere = RSA.Create(2048);

    public static SecurityKey CleSignature { get; } = new RsaSecurityKey(_rsa) { KeyId = "cle-essai" };

    /// <summary>Clé que l'API ne connaît pas : un jeton signé par elle est refusé.</summary>
    public static SecurityKey CleEtrangere { get; } = new RsaSecurityKey(_rsaEtrangere) { KeyId = "inconnue" };

    /// <summary>Jeton valide pour l'API de requête : audience et scope <c>ledger:read</c>.</summary>
    public static string Valide() => Creer();

    public static string Creer(string? portee = "openid ledger:read", string audience = Audience, SecurityKey? cle = null)
    {
        var claims = new List<Claim> { new("sub", Guid.NewGuid().ToString()), new("azp", "tableau-de-bord") };
        if (portee is not null)
        {
            claims.Add(new Claim("scope", portee));
        }

        var maintenant = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Emetteur,
            Audience = audience,
            Subject = new ClaimsIdentity(claims),
            NotBefore = maintenant.AddMinutes(-1),
            IssuedAt = maintenant.AddMinutes(-1),
            Expires = maintenant.AddMinutes(5),
            SigningCredentials = new SigningCredentials(cle ?? CleSignature, SecurityAlgorithms.RsaSha256),
        });
    }
}
