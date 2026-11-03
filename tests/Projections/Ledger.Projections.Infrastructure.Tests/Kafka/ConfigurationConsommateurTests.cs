using Confluent.Kafka;
using Ledger.Projections.Infrastructure.Kafka;

namespace Ledger.Projections.Infrastructure.Tests.Kafka;

public sealed class ConfigurationConsommateurTests
{
    private static KafkaSourceOptions Options(SecurityProtocol protocole = SecurityProtocol.Ssl) =>
        new() { BootstrapServers = "kafka-1.ledger-dev.internal:9093", SecurityProtocol = protocole };

    [Fact]
    public void Mtls_avec_certificat_client_cle_et_racine_et_verification_du_nom()
    {
        var options = Options();
        options.CertificatClientPem = "CHAINE";
        options.CleClientPem = "CLE";
        options.RacinePem = "RACINE";

        var config = KafkaEventSource.ConfigurationConsommateur(options);

        Assert.Equal(SecurityProtocol.Ssl, config.SecurityProtocol);
        Assert.Equal(("CHAINE", "CLE", "RACINE"), (config.SslCertificatePem, config.SslKeyPem, config.SslCaPem));
        Assert.Equal(SslEndpointIdentificationAlgorithm.Https, config.SslEndpointIdentificationAlgorithm);
        Assert.Equal(IsolationLevel.ReadCommitted, config.IsolationLevel);
        Assert.False(config.EnableAutoOffsetStore);
    }

    [Fact]
    public void Certificat_client_sans_cle_ou_sans_racine_est_refuse()
    {
        var options = Options();
        options.CertificatClientPem = "CHAINE";
        options.CleClientPem = "CLE";

        Assert.Throws<InvalidOperationException>(() => KafkaEventSource.ConfigurationConsommateur(options));
    }

    [Fact]
    public void Plaintext_local_sans_reglage_tls()
    {
        var config = KafkaEventSource.ConfigurationConsommateur(Options(SecurityProtocol.Plaintext));

        Assert.Null(config.SslCertificatePem);
        Assert.Equal("ledger-projections", config.GroupId);
    }
}
