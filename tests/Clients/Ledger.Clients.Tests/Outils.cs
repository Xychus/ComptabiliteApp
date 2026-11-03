using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Ledger.Clients.Tests;

internal sealed class HorlogeFixe(DateTimeOffset maintenant) : TimeProvider
{
    public DateTimeOffset Maintenant { get; set; } = maintenant;

    public override DateTimeOffset GetUtcNow() => Maintenant;
}

/// <summary>Serveur HTTP en mémoire : répond selon le numéro d'appel et garde chaque requête reçue.</summary>
internal sealed class ServeurSimule(Func<int, HttpRequestMessage, HttpResponseMessage> repondre) : HttpMessageHandler
{
    public List<(HttpMethod Methode, string? Autorisation, string? Corps)> Recues { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var corps = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Recues)
        {
            Recues.Add((request.Method, request.Headers.Authorization?.ToString(), corps));
            return repondre(Recues.Count, request);
        }
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode statut = HttpStatusCode.OK) =>
        new(statut) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

internal static class Exemples
{
    public static readonly DateTimeOffset Maintenant = new(2030, 1, 15, 12, 0, 0, TimeSpan.Zero);

    public static OptionsClientGrandLivre Options(string? portees = "ledger:read ledger:write", int tentatives = 5)
    {
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return new OptionsClientGrandLivre
        {
            PointTerminaisonJetons = new Uri("https://keycloak.ledger-test.internal/realms/ledger/protocol/openid-connect/token"),
            ClientId = "ledger-exemple",
            CertificatClient = new CertificateRequest("CN=ledger-exemple", cle, HashAlgorithmName.SHA256).CreateSelfSigned(Maintenant, Maintenant.AddDays(7)),
            Portees = portees,
            AdresseApi = new Uri("https://api.ledger-test.internal/v1/"),
            TentativesMaximum = tentatives,
        };
    }

    public static string ReponseJeton(string jeton, int duree = 300) => $$"""{"access_token":"{{jeton}}","expires_in":{{duree}},"token_type":"Bearer"}""";
}
