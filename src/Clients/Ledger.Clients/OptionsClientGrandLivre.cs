using System.Security.Cryptography.X509Certificates;

namespace Ledger.Clients;

/// <summary>Réglages d'une application interne cliente du grand livre (ADR 0030, 0031).</summary>
public sealed class OptionsClientGrandLivre
{
    /// <summary>Point de terminaison des jetons du realm : <c>…/realms/ledger/protocol/openid-connect/token</c>.</summary>
    public required Uri PointTerminaisonJetons { get; init; }

    /// <summary>Identifiant du client dans le realm ; il devient l'<c>actor</c> des événements.</summary>
    public required string ClientId { get; init; }

    /// <summary>Certificat <c>client</c> de la PKI et ses intermédiaires : authentifie le client auprès de Keycloak (mTLS).</summary>
    public required IdentiteClientPki IdentiteClient { get; init; }

    /// <summary>Racine de la PKI de l'environnement ; <c>null</c> : magasin du système (local).</summary>
    public X509Certificate2? RacinePki { get; init; }

    /// <summary>Scopes demandés, séparés par des espaces ; <c>null</c> : scopes par défaut du client.</summary>
    public string? Portees { get; init; }

    /// <summary>Adresse de l'API (API Gateway privée), étape comprise, terminée par <c>/</c>.</summary>
    public required Uri AdresseApi { get; init; }

    /// <summary>Le jeton est renouvelé quand il lui reste moins que cette marge.</summary>
    public TimeSpan MargeExpiration { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Nombre total d'envois d'une requête refusée par <c>429</c> (premier envoi compris).</summary>
    public int TentativesMaximum { get; init; } = 5;

    public TimeSpan DelaiInitial { get; init; } = TimeSpan.FromMilliseconds(200);

    public TimeSpan DelaiMaximum { get; init; } = TimeSpan.FromSeconds(10);
}
