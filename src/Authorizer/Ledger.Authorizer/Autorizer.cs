namespace Ledger.Authorizer;

/// <summary>
/// Refus d'authentification : le message exact « Unauthorized » fait répondre <c>401</c> par
/// API Gateway (contrat AWS) ; tout autre message donnerait une erreur <c>500</c>.
/// </summary>
public sealed class NonAutoriseException : Exception
{
    public const string MessageApiGateway = "Unauthorized";

    public NonAutoriseException()
        : base(MessageApiGateway)
    {
    }

    public NonAutoriseException(string message)
        : base(MessageApiGateway, new InvalidOperationException(message))
    {
    }

    public NonAutoriseException(string message, Exception innerException)
        : base(MessageApiGateway, innerException)
    {
    }
}

/// <summary>
/// Autorizer de l'API privée (ADR 0031) : jeton absent ou invalide → <c>401</c> ; jeton valide
/// sans scope du grand livre ou sans plan d'usage → politique de refus (<c>403</c>) ; sinon,
/// politique d'autorisation sur toutes les routes des scopes, clé du plan d'usage et
/// <c>context.clientId</c> (= <c>azp</c> = <c>actor</c> des événements).
/// </summary>
public sealed class Autorizer
{
    private const string PrefixeBearer = "Bearer ";

    private readonly ValidateurJetons _validateur;
    private readonly IMagasinClesPlanUsage _cles;

    public Autorizer(ValidateurJetons validateur, IMagasinClesPlanUsage cles)
    {
        ArgumentNullException.ThrowIfNull(validateur);
        ArgumentNullException.ThrowIfNull(cles);
        _validateur = validateur;
        _cles = cles;
    }

    public async Task<ReponseAutorisation> AutoriserAsync(EvenementAutorisation evenement, CancellationToken annulation)
    {
        ArgumentNullException.ThrowIfNull(evenement);

        var methodArn = evenement.MethodArn ?? throw new ArgumentException("methodArn absent de l'événement.", nameof(evenement));
        var jeton = ExtraireJeton(evenement.Headers) ?? throw new NonAutoriseException("En-tête Authorization absent ou mal formé.");
        var valide = await _validateur.ValiderAsync(jeton, annulation).ConfigureAwait(false)
            ?? throw new NonAutoriseException("Jeton refusé.");

        var ressources = PolitiqueRoutes.Ressources(methodArn, valide.Portees);
        if (ressources.Count == 0)
        {
            return Refus(valide.ClientId, methodArn);
        }

        var cle = await _cles.TrouverAsync(valide.ClientId, annulation).ConfigureAwait(false);
        if (cle is null)
        {
            return Refus(valide.ClientId, methodArn);
        }

        return new ReponseAutorisation
        {
            PrincipalId = valide.ClientId,
            PolicyDocument = new DocumentPolitique
            {
                Statement = [new DeclarationPolitique { Effect = "Allow", Resource = ressources }],
            },
            UsageIdentifierKey = cle,
            Context = new Dictionary<string, string>(StringComparer.Ordinal) { ["clientId"] = valide.ClientId },
        };
    }

    /// <summary>Jeton de l'en-tête <c>Authorization: Bearer …</c> (nom et schéma insensibles à la casse).</summary>
    internal static string? ExtraireJeton(IReadOnlyDictionary<string, string>? entetes)
    {
        var valeur = entetes?.FirstOrDefault(e => string.Equals(e.Key, "Authorization", StringComparison.OrdinalIgnoreCase)).Value;
        if (valeur is null || !valeur.StartsWith(PrefixeBearer, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var jeton = valeur[PrefixeBearer.Length..].Trim();
        return jeton.Length > 0 ? jeton : null;
    }

    private static ReponseAutorisation Refus(string clientId, string methodArn) => new()
    {
        PrincipalId = clientId,
        PolicyDocument = new DocumentPolitique
        {
            Statement = [new DeclarationPolitique { Effect = "Deny", Resource = [PolitiqueRoutes.ToutesLesRoutes(methodArn)] }],
        },
    };
}
