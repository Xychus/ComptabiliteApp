using System.Text.Json.Serialization;

namespace Ledger.Authorizer;

/// <summary>
/// Événement d'un autorizer Lambda de type <c>REQUEST</c> d'une API REST (contrat AWS, champs en
/// anglais). Seuls les champs lus sont déclarés.
/// </summary>
public sealed class EvenementAutorisation
{
    /// <summary>ARN de la méthode appelée : <c>arn:aws:execute-api:{région}:{compte}:{api}/{étape}/{MÉTHODE}/{chemin}</c>.</summary>
    [JsonPropertyName("methodArn")]
    public string? MethodArn { get; init; }

    /// <summary>En-têtes de la requête ; la casse des noms dépend du client.</summary>
    [JsonPropertyName("headers")]
    public Dictionary<string, string>? Headers { get; init; }
}

/// <summary>Réponse de l'autorizer : principal, politique IAM, clé du plan d'usage, contexte transmis à l'intégration.</summary>
public sealed class ReponseAutorisation
{
    [JsonPropertyName("principalId")]
    public required string PrincipalId { get; init; }

    [JsonPropertyName("policyDocument")]
    public required DocumentPolitique PolicyDocument { get; init; }

    /// <summary>Valeur de la clé API du plan d'usage (<c>api_key_source = "AUTHORIZER"</c>).</summary>
    [JsonPropertyName("usageIdentifierKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UsageIdentifierKey { get; init; }

    [JsonPropertyName("context")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Dictionary<string, string>? Context { get; init; }
}

public sealed class DocumentPolitique
{
    [JsonPropertyName("Version")]
    public string Version { get; init; } = "2012-10-17";

    [JsonPropertyName("Statement")]
    public required IReadOnlyList<DeclarationPolitique> Statement { get; init; }
}

public sealed class DeclarationPolitique
{
    [JsonPropertyName("Action")]
    public string Action { get; init; } = "execute-api:Invoke";

    [JsonPropertyName("Effect")]
    public required string Effect { get; init; }

    [JsonPropertyName("Resource")]
    public required IReadOnlyList<string> Resource { get; init; }
}

[JsonSerializable(typeof(EvenementAutorisation))]
[JsonSerializable(typeof(ReponseAutorisation))]
public sealed partial class ContexteJsonAutorisation : JsonSerializerContext;
