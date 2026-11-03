namespace Ledger.Pki.Application.Rotation;

/// <summary>Étiquettes de version du protocole de rotation de Secrets Manager.</summary>
public static class EtiquettesVersion
{
    public const string Courante = "AWSCURRENT";
    public const string EnAttente = "AWSPENDING";
}

/// <summary>État d'un secret de certificat : rotation, versions et leurs étiquettes, étiquettes de ressource (profil).</summary>
public sealed record DescriptionSecret(
    string Identifiant,
    bool RotationActivee,
    IReadOnlyDictionary<string, IReadOnlyList<string>> EtiquettesParVersion,
    IReadOnlyDictionary<string, string> Etiquettes)
{
    public bool VersionPorte(string version, string etiquette) =>
        EtiquettesParVersion.TryGetValue(version, out var etiquettes) && etiquettes.Contains(etiquette, StringComparer.Ordinal);

    public string? VersionPortant(string etiquette) =>
        EtiquettesParVersion.FirstOrDefault(v => v.Value.Contains(etiquette, StringComparer.Ordinal)).Key;
}

/// <summary>
/// Port du magasin des secrets de certificats (Secrets Manager en production). Interface limitée
/// aux opérations du protocole de rotation : aucune création ni suppression de secret.
/// </summary>
public interface IMagasinCertificats
{
    Task<DescriptionSecret> DecrireAsync(string secret, CancellationToken cancellationToken);

    /// <summary>Valeur d'une version désignée par son étiquette ; <see langword="null"/> si aucune version ne la porte.</summary>
    Task<string?> LireParEtiquetteAsync(string secret, string etiquette, CancellationToken cancellationToken);

    /// <summary>Valeur d'une version désignée par son identifiant ; <see langword="null"/> si elle n'existe pas.</summary>
    Task<string?> LireVersionAsync(string secret, string version, CancellationToken cancellationToken);

    /// <summary>Crée la version <paramref name="version"/> avec l'étiquette <see cref="EtiquettesVersion.EnAttente"/>.</summary>
    Task EcrireEnAttenteAsync(string secret, string version, string valeur, CancellationToken cancellationToken);

    /// <summary>Déplace <see cref="EtiquettesVersion.Courante"/> de <paramref name="versionCourante"/> vers <paramref name="version"/>.</summary>
    Task PromouvoirAsync(string secret, string version, string? versionCourante, CancellationToken cancellationToken);
}
