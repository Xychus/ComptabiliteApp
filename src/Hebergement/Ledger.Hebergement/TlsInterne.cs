using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Configuration;

namespace Ledger.Hebergement;

/// <summary>Certificat serveur de la PKI interne (section <c>Ledger:Tls</c>, variables injectées par <c>secrets</c> ECS).</summary>
public sealed class OptionsTlsInterne
{
    public const string Section = "Ledger:Tls";

    /// <summary>Certificat suivi de l'intermédiaire (champ <c>chaine</c> du secret, ADR 0010).</summary>
    public string? Chaine { get; set; }

    /// <summary>Clé privée PKCS#8 (champ <c>cle</c> du secret).</summary>
    public string? Cle { get; set; }

    public int PortHttps { get; set; } = 8443;
}

/// <summary>
/// HTTPS de service à service par Kestrel (ADR 0010) : sans TLS natif d'ECS Service Connect, chaque
/// service présente lui-même un certificat de la PKI interne. Sans certificat configuré (local,
/// tests), rien ne change : HTTP sur <c>ASPNETCORE_HTTP_PORTS</c>. Avec un certificat, Kestrel
/// n'écoute qu'en HTTPS. Le certificat est lu au démarrage ; la rotation redéploie la tâche.
/// </summary>
public static class TlsInterne
{
    // TLS 1.2 accepté en plus de 1.3 : l'équilibreur de charge applicatif d'AWS ne négocie pas
    // TLS 1.3 avec ses cibles (connexions dorsales). 1.3 reste négocié en priorité (CLAUDE.md).
    public const SslProtocols Protocoles = SslProtocols.Tls13 | SslProtocols.Tls12;

    public static WebApplicationBuilder AjouterTlsInterne(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = builder.Configuration.GetSection(OptionsTlsInterne.Section).Get<OptionsTlsInterne>();
        if (options?.Chaine is null)
        {
            return builder;
        }

        var certificat = Charger(options);
        builder.WebHost.ConfigureKestrel(kestrel =>
            kestrel.ListenAnyIP(options.PortHttps, ecoute => ecoute.UseHttps(new HttpsConnectionAdapterOptions
            {
                ServerCertificate = certificat,
                SslProtocols = Protocoles,
                ClientCertificateMode = ClientCertificateMode.NoCertificate,
            })));
        return builder;
    }

    /// <summary>Charge le certificat serveur ; échoue au démarrage si la clé manque ou ne correspond pas.</summary>
    public static X509Certificate2 Charger(OptionsTlsInterne options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.Chaine) || string.IsNullOrWhiteSpace(options.Cle))
        {
            throw new InvalidOperationException($"{OptionsTlsInterne.Section} : la chaîne et la clé du certificat sont exigées ensemble.");
        }

        // Réimport PKCS#12 en mémoire : SChannel (Windows, développement) refuse une clé éphémère
        // issue d'un PEM pour un serveur TLS ; sans effet sous Linux.
        using var depuisPem = X509Certificate2.CreateFromPem(options.Chaine, options.Cle);
        return X509CertificateLoader.LoadPkcs12(depuisPem.Export(X509ContentType.Pkcs12), password: null);
    }
}
