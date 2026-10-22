using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Ledger.Command.Api.Composition;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Ledger.Command.Api.Security;

/// <summary>Réglages de validation des jetons Keycloak (section <c>Ledger:Authentication</c>).</summary>
public sealed class LedgerAuthenticationOptions
{
    public const string Section = "Ledger:Authentication";

    /// <summary>URL du realm Keycloak (émetteur et découverte OIDC), en HTTPS.</summary>
    [Required]
    [Url]
    public string Authority { get; set; } = string.Empty;

    /// <summary>Audience exigée dans le jeton.</summary>
    [Required]
    public string Audience { get; set; } = LedgerAuthentication.DefaultAudience;
}

/// <summary>
/// Authentification en défense en profondeur (ADR 0014) : jeton JWT Keycloak validé par l'API
/// (émetteur, signature, durée de vie, audience), scope <c>ledger:write</c> et client appelant
/// (<c>azp</c>) exigés. L'autorizer de l'API Gateway reste le premier contrôle (phase 9).
/// </summary>
public static class LedgerAuthentication
{
    public const string WritePolicy = "ledger:write";
    public const string WriteScope = "ledger:write";
    public const string DefaultAudience = "ledger-api";

    /// <summary>Client OAuth qui a obtenu le jeton : il devient l'<c>actor</c> des événements.</summary>
    public const string ActorClaim = "azp";

    public static IServiceCollection AddLedgerAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var options = services.AddOptions<LedgerAuthenticationOptions>()
            .Bind(configuration.GetSection(LedgerAuthenticationOptions.Section))
            .ValidateDataAnnotations();
        if (!DocumentGeneration.IsRunning)
        {
            options.ValidateOnStart();
        }

        // L'API n'émet ni cookie ni charge protégée : aucun trousseau de clés n'est lu ni écrit
        // (système de fichiers en lecture seule dans le conteneur).
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<LedgerAuthenticationOptions>, IHostEnvironment>((jwt, ledger, environnement) =>
            {
                jwt.Authority = ledger.Value.Authority;
                jwt.Audience = ledger.Value.Audience;

                // Métadonnées OIDC en HTTP tolérées seulement en développement (Keycloak local, ADR 0020).
                jwt.RequireHttpsMetadata = !environnement.IsDevelopment();
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters.NameClaimType = ActorClaim;
                jwt.TokenValidationParameters.ValidateIssuer = true;
                jwt.TokenValidationParameters.ValidateAudience = true;
                jwt.TokenValidationParameters.ValidateLifetime = true;
                jwt.TokenValidationParameters.RequireSignedTokens = true;
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(WritePolicy, policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(ActorClaim)
                .RequireAssertion(context => HasScope(context.User, WriteScope)));
        return services;
    }

    /// <summary>
    /// Vrai si le jeton accorde <paramref name="scope"/> : claim <c>scope</c> en liste séparée par
    /// des espaces (Keycloak), ou plusieurs claims <c>scope</c> / <c>scp</c>.
    /// </summary>
    public static bool HasScope(ClaimsPrincipal user, string scope)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.FindAll(c => c.Type is "scope" or "scp")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(scope, StringComparer.Ordinal);
    }

    /// <summary>Client appelant, garanti présent par la politique <see cref="WritePolicy"/>.</summary>
    public static string Actor(ClaimsPrincipal user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return user.FindFirst(ActorClaim)?.Value ?? throw new InvalidOperationException($"Claim {ActorClaim} absent d'un jeton autorisé.");
    }
}
