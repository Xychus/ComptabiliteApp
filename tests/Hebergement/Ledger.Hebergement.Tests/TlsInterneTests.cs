using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Ledger.Hebergement.Tests;

public sealed class TlsInterneTests
{
    private static (string Chaine, string Cle) CertificatServeur()
    {
        using var cle = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var demande = new CertificateRequest("CN=query-api", cle, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        demande.CertificateExtensions.Add(san.Build());
        using var certificat = demande.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7));
        return (certificat.ExportCertificatePem(), cle.ExportPkcs8PrivateKeyPem());
    }

    [Fact]
    public void Charger_sans_cle_echoue() =>
        Assert.Throws<InvalidOperationException>(() => TlsInterne.Charger(new OptionsTlsInterne { Chaine = CertificatServeur().Chaine }));

    [Fact]
    public void Charger_avec_cle_appariee()
    {
        var (chaine, cle) = CertificatServeur();

        using var certificat = TlsInterne.Charger(new OptionsTlsInterne { Chaine = chaine, Cle = cle });

        Assert.True(certificat.HasPrivateKey);
    }

    [Fact]
    public void Sans_certificat_la_configuration_ne_change_pas()
    {
        var builder = WebApplication.CreateBuilder();

        Assert.Same(builder, builder.AjouterTlsInterne());
    }

    [Fact]
    public async Task Avec_certificat_kestrel_ecoute_en_https_et_negocie_tls13()
    {
        var (chaine, cle) = CertificatServeur();
        var port = PortLibre();
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Ledger:Tls:Chaine"] = chaine;
        builder.Configuration["Ledger:Tls:Cle"] = cle;
        builder.Configuration["Ledger:Tls:PortHttps"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        await using var app = builder.AjouterTlsInterne().Build();
        app.MapGet("/", () => Results.Ok("ok"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, TestContext.Current.CancellationToken);
        await using var tls = new SslStream(client.GetStream(), false, (_, certificat, _, _) => certificat?.Subject == "CN=query-api");
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, TestContext.Current.CancellationToken);

        Assert.Equal(SslProtocols.Tls13, tls.SslProtocol);
        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    private static int PortLibre()
    {
        using var ecoute = new TcpListener(IPAddress.Loopback, 0);
        ecoute.Start();
        return ((IPEndPoint)ecoute.LocalEndpoint).Port;
    }
}
