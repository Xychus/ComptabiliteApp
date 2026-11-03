namespace Ledger.Authorizer;

/// <summary>Réglages de l'autorizer, lus dans les variables d'environnement de la Lambda (chiffrées par la CMK <c>lambda</c>).</summary>
public sealed class OptionsAutorizer
{
    /// <summary>URL du realm Keycloak : émetteur exigé et base de la découverte OIDC.</summary>
    public required string Emetteur { get; init; }

    /// <summary>Audience exigée dans le jeton.</summary>
    public string Audience { get; init; } = "ledger-api";

    /// <summary>Préfixe des paramètres SSM des clés de plan d'usage (un paramètre par <c>azp</c>).</summary>
    public required string PrefixeClesPlanUsage { get; init; }

    /// <summary>Racine de la PKI interne (PEM, publique) : seule autorité admise pour joindre Keycloak.</summary>
    public string? CertificatRacinePki { get; init; }

    /// <summary>Durée de conservation en mémoire d'une clé de plan d'usage lue dans SSM.</summary>
    public TimeSpan DureeCacheCles { get; init; } = TimeSpan.FromMinutes(5);

    public static OptionsAutorizer DepuisEnvironnement(Func<string, string?> lire)
    {
        ArgumentNullException.ThrowIfNull(lire);

        var emetteur = Exiger(lire, "LEDGER_EMETTEUR");
        if (!Uri.TryCreate(emetteur, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("LEDGER_EMETTEUR : URL HTTPS du realm attendue.");
        }

        return new OptionsAutorizer
        {
            Emetteur = emetteur,
            Audience = lire("LEDGER_AUDIENCE") is { Length: > 0 } audience ? audience : "ledger-api",
            PrefixeClesPlanUsage = Exiger(lire, "LEDGER_PREFIXE_CLES_PLAN_USAGE").TrimEnd('/'),
            CertificatRacinePki = lire("LEDGER_RACINE_PKI") is { Length: > 0 } racine ? racine : null,
        };
    }

    private static string Exiger(Func<string, string?> lire, string nom) =>
        lire(nom) is { Length: > 0 } valeur ? valeur : throw new InvalidOperationException($"Variable d'environnement {nom} absente.");
}
