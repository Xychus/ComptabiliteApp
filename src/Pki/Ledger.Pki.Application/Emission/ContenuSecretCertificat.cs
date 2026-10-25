using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ledger.Pki.Application.Emission;

/// <summary>
/// Valeur JSON d'un secret de certificat final. Chaque champ est injecté séparément dans les
/// conteneurs par <c>secrets</c> des task definitions (<c>valueFrom = "&lt;arn&gt;:cle::"</c>).
/// </summary>
/// <param name="Certificat">Certificat final (PEM).</param>
/// <param name="Chaine">Certificat final suivi de l'intermédiaire (PEM) : ce que le serveur ou le client présente.</param>
/// <param name="Cle">Clé privée PKCS#8 non chiffrée (PEM), format exigé par le keystore PEM de Kafka.</param>
/// <param name="Racine">Certificat racine (PEM) : seule ancre de confiance des magasins.</param>
/// <param name="ExpireLe">Échéance du certificat final.</param>
public sealed record ContenuSecretCertificat(
    [property: JsonPropertyName("certificat")] string Certificat,
    [property: JsonPropertyName("chaine")] string Chaine,
    [property: JsonPropertyName("cle")] string Cle,
    [property: JsonPropertyName("racine")] string Racine,
    [property: JsonPropertyName("expire_le")] DateTimeOffset ExpireLe)
{
    public string VersJson() => JsonSerializer.Serialize(this);

    /// <summary>Lit un contenu ; <see langword="null"/> si le JSON est invalide ou incomplet.</summary>
    public static ContenuSecretCertificat? DepuisJson(string json)
    {
        try
        {
            var contenu = JsonSerializer.Deserialize<ContenuSecretCertificat>(json);
            return contenu is { Certificat: not null, Chaine: not null, Cle: not null, Racine: not null } ? contenu : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public override string ToString() => $"ContenuSecretCertificat {{ ExpireLe = {ExpireLe:O} }}";
}
