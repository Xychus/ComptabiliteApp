using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ledger.Pki.Application.Rotation;
using Ledger.SharedKernel;

namespace Ledger.Pki.Application.MotsDePasse;

/// <summary>Valeur JSON d'un secret de mot de passe PostgreSQL (champ <c>mot_de_passe</c> injecté par <c>secrets</c> ECS).</summary>
public sealed record ContenuMotDePasse(
    [property: JsonPropertyName("utilisateur")] string Utilisateur,
    [property: JsonPropertyName("mot_de_passe")] string MotDePasse)
{
    public string VersJson() => JsonSerializer.Serialize(this);

    public static ContenuMotDePasse? DepuisJson(string json)
    {
        try
        {
            var contenu = JsonSerializer.Deserialize<ContenuMotDePasse>(json);
            return contenu is { Utilisateur.Length: > 0, MotDePasse.Length: > 0 } ? contenu : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public override string ToString() => $"ContenuMotDePasse {{ Utilisateur = {Utilisateur} }}";
}

/// <summary>
/// Port d'administration des rôles PostgreSQL (connexion avec le rôle maître d'Aurora, dont le
/// secret est géré par RDS).
/// </summary>
public interface IAdministrationRolesPostgres
{
    /// <summary>Donne LOGIN et le mot de passe au rôle.</summary>
    Task DefinirMotDePasseAsync(string role, string motDePasse, CancellationToken cancellationToken);

    /// <summary>Vrai si le rôle peut se connecter avec ce mot de passe.</summary>
    Task<bool> TesterConnexionAsync(string role, string motDePasse, CancellationToken cancellationToken);
}

/// <summary>Rôle PostgreSQL d'un secret de mot de passe, lu dans l'étiquette <see cref="Etiquette"/>.</summary>
public static partial class RolePostgres
{
    public const string Etiquette = "pg:role";

    public static Result<string> De(DescriptionSecret description)
    {
        ArgumentNullException.ThrowIfNull(description);
        return description.Etiquettes.TryGetValue(Etiquette, out var role) && Identifiant().IsMatch(role)
            ? Result.Success(role)
            : Result.Failure<string>(new DomainError("pki.mot-de-passe.role", $"Étiquette « {Etiquette} » absente ou invalide."));
    }

    [GeneratedRegex("^[a-z_][a-z0-9_]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifiant();
}

/// <summary><c>createSecret</c> : nouveau mot de passe aléatoire (40 caractères alphanumériques), en attente.</summary>
public sealed class CreationMotDePasse(IMagasinCertificats magasin) : IEtapeRotation
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    public string TypeSecret => TypesSecret.MotDePassePostgres;

    public EtapeRotation Etape => EtapeRotation.CreerSecret;

    public async Task<Result<string>> ExecuterAsync(DemandeRotation demande, DescriptionSecret description, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demande);

        if (await magasin.LireVersionAsync(demande.Secret, demande.Version, cancellationToken).ConfigureAwait(false) is not null)
        {
            return Result.Success($"Version {demande.Version} déjà créée.");
        }

        var role = RolePostgres.De(description);
        if (role.IsFailure)
        {
            return Result.Failure<string>(role.Error);
        }

        var contenu = new ContenuMotDePasse(role.Value, RandomNumberGenerator.GetString(Alphabet, 40));
        await magasin.EcrireEnAttenteAsync(demande.Secret, demande.Version, contenu.VersJson(), cancellationToken).ConfigureAwait(false);
        return Result.Success($"Mot de passe généré pour {role.Value}.");
    }
}

/// <summary>
/// <c>setSecret</c> : applique le mot de passe en attente au rôle. L'ancien cesse de fonctionner :
/// les connexions établies restent ouvertes et le service est redéployé à la fin de la rotation.
/// </summary>
public sealed class DefinitionMotDePasse(IMagasinCertificats magasin, IAdministrationRolesPostgres administration) : IEtapeRotation
{
    public string TypeSecret => TypesSecret.MotDePassePostgres;

    public EtapeRotation Etape => EtapeRotation.DefinirSecret;

    public async Task<Result<string>> ExecuterAsync(DemandeRotation demande, DescriptionSecret description, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demande);

        var contenu = await EtapesMotDePasse.LireEnAttenteAsync(magasin, demande, description, cancellationToken).ConfigureAwait(false);
        if (contenu.IsFailure)
        {
            return Result.Failure<string>(contenu.Error);
        }

        await administration.DefinirMotDePasseAsync(contenu.Value.Utilisateur, contenu.Value.MotDePasse, cancellationToken).ConfigureAwait(false);
        return Result.Success($"Mot de passe de {contenu.Value.Utilisateur} appliqué.");
    }
}

/// <summary><c>testSecret</c> : le rôle se connecte avec le mot de passe en attente.</summary>
public sealed class TestMotDePasse(IMagasinCertificats magasin, IAdministrationRolesPostgres administration) : IEtapeRotation
{
    public string TypeSecret => TypesSecret.MotDePassePostgres;

    public EtapeRotation Etape => EtapeRotation.TesterSecret;

    public async Task<Result<string>> ExecuterAsync(DemandeRotation demande, DescriptionSecret description, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demande);

        var contenu = await EtapesMotDePasse.LireEnAttenteAsync(magasin, demande, description, cancellationToken).ConfigureAwait(false);
        if (contenu.IsFailure)
        {
            return Result.Failure<string>(contenu.Error);
        }

        return await administration.TesterConnexionAsync(contenu.Value.Utilisateur, contenu.Value.MotDePasse, cancellationToken).ConfigureAwait(false)
            ? Result.Success($"Connexion de {contenu.Value.Utilisateur} vérifiée.")
            : Result.Failure<string>(new DomainError("pki.mot-de-passe.connexion", $"{contenu.Value.Utilisateur} ne peut pas se connecter avec le nouveau mot de passe."));
    }
}

internal static class EtapesMotDePasse
{
    public static async Task<Result<ContenuMotDePasse>> LireEnAttenteAsync(
        IMagasinCertificats magasin, DemandeRotation demande, DescriptionSecret description, CancellationToken cancellationToken)
    {
        var valeur = await magasin.LireVersionAsync(demande.Secret, demande.Version, cancellationToken).ConfigureAwait(false);
        if (valeur is null || ContenuMotDePasse.DepuisJson(valeur) is not { } contenu)
        {
            return Result.Failure<ContenuMotDePasse>(new DomainError("pki.rotation.contenu", $"Version {demande.Version} absente ou illisible."));
        }

        var role = RolePostgres.De(description);
        if (role.IsFailure)
        {
            return Result.Failure<ContenuMotDePasse>(role.Error);
        }

        // Le rôle du contenu doit être celui de l'étiquette : un contenu ne choisit pas sa cible.
        return contenu.Utilisateur == role.Value
            ? Result.Success(contenu)
            : Result.Failure<ContenuMotDePasse>(new DomainError("pki.mot-de-passe.role", $"Le contenu vise {contenu.Utilisateur}, l'étiquette {role.Value}."));
    }
}
