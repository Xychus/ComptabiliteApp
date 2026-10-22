using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Ledger.Query.Api.Composition;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Ledger.Query.Api.Securite;

/// <summary>
/// Réglages de validation des jetons Keycloak : même section et mêmes clés que l'API de commandes
/// (<c>Ledger:Authentication</c>), partagées par la configuration de déploiement.
/// </summary>
public sealed class OptionsAuthentification
{
    public const string Section = "Ledger:Authentication";

    /// <summary>URL du realm Keycloak (émetteur et découverte OIDC), en HTTPS.</summary>
    [Required]
    [Url]
    public string Authority { get; set; } = string.Empty;

    /// <summary>Audience exigée dans le jeton.</summary>
    [Required]
    public string Audience { get; set; } = AuthentificationRequete.AudienceParDefaut;
}

/// <summary>
/// Authentification en défense en profondeur (ADR 0019) : jeton JWT Keycloak validé par l'API
/// (émetteur, signature, durée de vie, audience) et scope <c>ledger:read</c> exigé. L'autorizer de
/// l'API Gateway reste le premier contrôle (phase 9). Le scope <c>ledger:write</c> n'accorde pas la lecture.
/// </summary>
public static class AuthentificationRequete
{
    public const string PolitiqueLecture = "ledger:read";
    public const string PorteeLecture = "ledger:read";
    public const string AudienceParDefaut = "ledger-api";

    public static IServiceCollection AjouterAuthentificationRequete(this IServiceCollection services, IConfiguration configuration)
    {
        var options = services.AddOptions<OptionsAuthentification>()
            .Bind(configuration.GetSection(OptionsAuthentification.Section))
            .ValidateDataAnnotations();
        if (!GenerationDocument.EnCours)
        {
            options.ValidateOnStart();
        }

        // L'API n'émet ni cookie ni charge protégée : aucun trousseau de clés n'est lu ni écrit
        // (système de fichiers en lecture seule dans le conteneur).
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<OptionsAuthentification>, IHostEnvironment>((jwt, reglages, environnement) =>
            {
                jwt.Authority = reglages.Value.Authority;
                jwt.Audience = reglages.Value.Audience;

                // Métadonnées OIDC en HTTP tolérées seulement en développement (Keycloak local, ADR 0020).
                jwt.RequireHttpsMetadata = !environnement.IsDevelopment();
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters.NameClaimType = "azp";
                jwt.TokenValidationParameters.ValidateIssuer = true;
                jwt.TokenValidationParameters.ValidateAudience = true;
                jwt.TokenValidationParameters.ValidateLifetime = true;
                jwt.TokenValidationParameters.RequireSignedTokens = true;
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(PolitiqueLecture, politique => politique
                .RequireAuthenticatedUser()
                .RequireAssertion(contexte => APortee(contexte.User, PorteeLecture)));
        return services;
    }

    /// <summary>
    /// Vrai si le jeton accorde <paramref name="portee"/> : claim <c>scope</c> en liste séparée par
    /// des espaces (Keycloak), ou plusieurs claims <c>scope</c> / <c>scp</c>.
    /// </summary>
    public static bool APortee(ClaimsPrincipal utilisateur, string portee)
    {
        ArgumentNullException.ThrowIfNull(utilisateur);
        return utilisateur.FindAll(c => c.Type is "scope" or "scp")
            .SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(portee, StringComparer.Ordinal);
    }
}
