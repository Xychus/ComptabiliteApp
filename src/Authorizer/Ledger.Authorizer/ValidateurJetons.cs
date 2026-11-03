using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Ledger.Authorizer;

/// <summary>Jeton accepté : client appelant (<c>azp</c>) et scopes accordés.</summary>
public sealed record JetonValide(string ClientId, IReadOnlySet<string> Portees);

/// <summary>
/// Valide un jeton d'accès Keycloak : signature asymétrique par les clés du realm (configuration
/// OIDC mise en cache par <see cref="IConfigurationManager{T}"/>), émetteur, audience, durée de
/// vie, <c>azp</c> présent. Un <c>kid</c> inconnu force une seule relecture des clés (rotation ou
/// réimport du realm, ADR 0030).
/// </summary>
public sealed class ValidateurJetons
{
    // Algorithmes asymétriques seulement : ni « none » ni HMAC (clé publique utilisée comme secret).
    private static readonly string[] AlgorithmesAcceptes =
    [
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSsaPssSha256,
    ];

    private readonly IConfigurationManager<OpenIdConnectConfiguration> _configuration;
    private readonly OptionsAutorizer _options;
    private readonly TimeProvider _horloge;
    private readonly JsonWebTokenHandler _gestionnaire = new() { MapInboundClaims = false };

    public ValidateurJetons(IConfigurationManager<OpenIdConnectConfiguration> configuration, OptionsAutorizer options, TimeProvider horloge)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(horloge);
        _configuration = configuration;
        _options = options;
        _horloge = horloge;
    }

    /// <summary>Jeton valide, ou <c>null</c> si le jeton est refusé (quelle qu'en soit la raison).</summary>
    public async Task<JetonValide?> ValiderAsync(string jeton, CancellationToken annulation)
    {
        ArgumentNullException.ThrowIfNull(jeton);

        var resultat = await ValiderAvecClesAsync(jeton, annulation).ConfigureAwait(false);
        if (resultat.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            _configuration.RequestRefresh();
            resultat = await ValiderAvecClesAsync(jeton, annulation).ConfigureAwait(false);
        }

        if (!resultat.IsValid || resultat.SecurityToken is not JsonWebToken jwt)
        {
            return null;
        }

        if (!jwt.TryGetPayloadValue<string>("azp", out var clientId) || string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        var portees = jwt.TryGetPayloadValue<string>("scope", out var scope)
            ? scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal)
            : [];
        return new JetonValide(clientId, portees);
    }

    private async Task<TokenValidationResult> ValiderAvecClesAsync(string jeton, CancellationToken annulation)
    {
        var configuration = await _configuration.GetConfigurationAsync(annulation).ConfigureAwait(false);
        var parametres = new TokenValidationParameters
        {
            ValidIssuer = _options.Emetteur,
            ValidAudience = _options.Audience,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidAlgorithms = AlgorithmesAcceptes,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            // Jetons de 5 minutes (ADR 0030) : la tolérance par défaut (5 minutes) doublerait leur durée.
            ClockSkew = TimeSpan.FromSeconds(30),
            LifetimeValidator = (avant, expiration, _, parametresValidation) => ValiderDuree(avant, expiration, parametresValidation.ClockSkew),
        };
        return await _gestionnaire.ValidateTokenAsync(jeton, parametres).ConfigureAwait(false);
    }

    // Durée de vie évaluée sur l'horloge injectée (testable) plutôt que sur l'horloge système.
    private bool ValiderDuree(DateTime? avant, DateTime? expiration, TimeSpan tolerance)
    {
        var maintenant = _horloge.GetUtcNow().UtcDateTime;
        return expiration is { } fin && maintenant <= fin + tolerance && (avant is not { } debut || maintenant >= debut - tolerance);
    }
}
