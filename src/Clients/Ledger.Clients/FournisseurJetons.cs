using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Ledger.Clients;

/// <summary>Échec d'obtention d'un jeton auprès de Keycloak (statut et code d'erreur OAuth, sans le corps).</summary>
public sealed class EchecJetonException : Exception
{
    public EchecJetonException()
    {
    }

    public EchecJetonException(string message)
        : base(message)
    {
    }

    public EchecJetonException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Jeton d'accès par <i>client credentials</i> (RFC 6749 §4.4), le client étant authentifié par
/// son certificat au niveau TLS (RFC 8705, ADR 0030) : seul <c>client_id</c> figure dans la
/// requête. Le jeton est conservé jusqu'à <see cref="OptionsClientGrandLivre.MargeExpiration"/>
/// avant son expiration ; une seule demande à la fois, partagée par les appels concurrents.
/// </summary>
public sealed class FournisseurJetons : IDisposable
{
    private readonly HttpClient _keycloak;
    private readonly OptionsClientGrandLivre _options;
    private readonly TimeProvider _horloge;
    private readonly SemaphoreSlim _verrou = new(1, 1);
    private (string Valeur, DateTimeOffset Renouveler)? _jeton;

    public FournisseurJetons(HttpClient keycloak, OptionsClientGrandLivre options, TimeProvider horloge)
    {
        ArgumentNullException.ThrowIfNull(keycloak);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(horloge);
        _keycloak = keycloak;
        _options = options;
        _horloge = horloge;
    }

    public async Task<string> ObtenirAsync(CancellationToken annulation)
    {
        if (Courant() is { } courant)
        {
            return courant;
        }

        await _verrou.WaitAsync(annulation).ConfigureAwait(false);
        try
        {
            // Un appel concurrent a pu obtenir le jeton pendant l'attente du verrou.
            if (Courant() is { } obtenu)
            {
                return obtenu;
            }

            var (valeur, duree) = await DemanderAsync(annulation).ConfigureAwait(false);
            _jeton = (valeur, _horloge.GetUtcNow() + duree - _options.MargeExpiration);
            return valeur;
        }
        finally
        {
            _verrou.Release();
        }
    }

    /// <summary>Oublie le jeton courant s'il vaut <paramref name="refuse"/> (refusé par l'API : realm réimporté, clés changées).</summary>
    public void Invalider(string refuse)
    {
        if (_jeton is { } jeton && jeton.Valeur == refuse)
        {
            _jeton = null;
        }
    }

    public void Dispose() => _verrou.Dispose();

    private string? Courant() => _jeton is { } jeton && _horloge.GetUtcNow() < jeton.Renouveler ? jeton.Valeur : null;

    private async Task<(string Valeur, TimeSpan Duree)> DemanderAsync(CancellationToken annulation)
    {
        var champs = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _options.ClientId,
        };
        if (!string.IsNullOrWhiteSpace(_options.Portees))
        {
            champs["scope"] = _options.Portees;
        }

        using var contenu = new FormUrlEncodedContent(champs);
        using var reponse = await _keycloak.PostAsync(_options.PointTerminaisonJetons, contenu, annulation).ConfigureAwait(false);
        if (!reponse.IsSuccessStatusCode)
        {
            var erreur = await LireErreurAsync(reponse, annulation).ConfigureAwait(false);
            throw new EchecJetonException($"Jeton refusé par {_options.PointTerminaisonJetons} : {(int)reponse.StatusCode} {erreur}".TrimEnd());
        }

        var jeton = await reponse.Content.ReadFromJsonAsync(ContexteJsonJetons.Default.ReponseJeton, annulation).ConfigureAwait(false);
        if (jeton?.AccessToken is not { Length: > 0 } valeur || jeton.ExpiresIn <= 0)
        {
            throw new EchecJetonException($"Réponse de jeton incomplète de {_options.PointTerminaisonJetons}.");
        }

        return (valeur, TimeSpan.FromSeconds(jeton.ExpiresIn));
    }

    // Code d'erreur OAuth seulement (ex. « unauthorized_client ») : la description peut citer le client.
    private static async Task<string> LireErreurAsync(HttpResponseMessage reponse, CancellationToken annulation)
    {
        try
        {
            var erreur = await reponse.Content.ReadFromJsonAsync(ContexteJsonJetons.Default.ReponseJeton, annulation).ConfigureAwait(false);
            return erreur?.Error ?? string.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
            return string.Empty;
        }
    }
}

internal sealed class ReponseJeton
{
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; init; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}

[JsonSerializable(typeof(ReponseJeton))]
internal sealed partial class ContexteJsonJetons : JsonSerializerContext;
