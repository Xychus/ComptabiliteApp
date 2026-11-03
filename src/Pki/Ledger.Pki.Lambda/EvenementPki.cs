using System.Text.Json.Serialization;

namespace Ledger.Pki.Lambda;

/// <summary>
/// Événement reçu par la Lambda. Deux sources :
/// <list type="bullet">
/// <item>Secrets Manager (rotation) : <c>SecretId</c>, <c>ClientRequestToken</c>, <c>Step</c> (contrat AWS, en anglais) ;</item>
/// <item>EventBridge Scheduler ou un administrateur : <c>operation</c> (<c>inventaire</c>,
/// <c>demande-signature-intermediaire</c>), avec <c>cle</c> et <c>sujet</c> pour la seconde.</item>
/// </list>
/// </summary>
public sealed class EvenementPki
{
    [JsonPropertyName("SecretId")]
    public string? SecretId { get; init; }

    [JsonPropertyName("ClientRequestToken")]
    public string? ClientRequestToken { get; init; }

    [JsonPropertyName("Step")]
    public string? Step { get; init; }

    [JsonPropertyName("operation")]
    public string? Operation { get; init; }

    [JsonPropertyName("cle")]
    public string? Cle { get; init; }

    [JsonPropertyName("sujet")]
    public string? Sujet { get; init; }
}

/// <summary>Réponse de la Lambda (visible dans les journaux et par l'administrateur qui l'invoque).</summary>
public sealed class ReponsePki
{
    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>Demande de signature PEM (opération <c>demande-signature-intermediaire</c>).</summary>
    [JsonPropertyName("csr")]
    public string? Csr { get; init; }
}

[JsonSerializable(typeof(EvenementPki))]
[JsonSerializable(typeof(ReponsePki))]
public sealed partial class ContexteJsonPki : JsonSerializerContext;
