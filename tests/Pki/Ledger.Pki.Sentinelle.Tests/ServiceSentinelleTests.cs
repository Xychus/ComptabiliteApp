using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Sentinelle;
using Ledger.Pki.Tests.Support;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Ledger.Pki.Sentinelle.Tests;

public sealed class ServiceSentinelleTests
{
    private static readonly OptionsSentinelle _options = new() { Intervalle = TimeSpan.FromMilliseconds(5), NombreCreneaux = 1 };

    [Fact]
    public async Task Nouveau_certificat_arrete_l_hote_avec_le_code_de_redemarrage()
    {
        var source = new SourceEssai { Version = "v2" };
        var cycleDeVie = new CycleDeVieEssai();
        using var service = Service(source, cycleDeVie);

        await service.StartAsync(TestContext.Current.CancellationToken);
        await cycleDeVie.Arret.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceSentinelle.CodeRedemarrage, Environment.ExitCode);
        Environment.ExitCode = 0;
    }

    [Fact]
    public async Task Panne_passagere_ne_redemarre_pas_et_la_surveillance_continue()
    {
        var source = new SourceEssai { Version = "v1", EchecsRestants = 2 };
        var cycleDeVie = new CycleDeVieEssai();
        using var service = Service(source, cycleDeVie);

        await service.StartAsync(TestContext.Current.CancellationToken);
        while (source.Lectures < 4)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.False(cycleDeVie.Arret.Task.IsCompleted);
    }

    private static ServiceSentinelle Service(SourceEssai source, CycleDeVieEssai cycleDeVie)
    {
        var sentinelle = new Application.Sentinelle.Sentinelle(source, new EcrivainEssai(), new SanteEssai(), new HorlogeEssai(AutoriteEssai.Maintenant), _options);
        return new ServiceSentinelle(sentinelle, _options, cycleDeVie, TimeProvider.System, NullLogger<ServiceSentinelle>.Instance);
    }

    private sealed class SourceEssai : ISourceCertificatCourant
    {
        public string Version { get; set; } = "v1";

        public int EchecsRestants { get; set; }

        public int Lectures { get; private set; }

        public Task<CertificatCourant> LireAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CertificatCourant(Version, new ContenuSecretCertificat("c", "c", "k", "r", AutoriteEssai.Maintenant.AddDays(7))));

        public Task<string> LireVersionAsync(CancellationToken cancellationToken)
        {
            Lectures++;
            if (EchecsRestants > 0)
            {
                EchecsRestants--;
                throw new InvalidOperationException("Secrets Manager indisponible.");
            }

            return Task.FromResult(Version);
        }
    }

    private sealed class EcrivainEssai : IEcrivainCertificats
    {
        public Task EcrireAsync(CertificatCourant certificat, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CertificatInstalle?> LireInstalleAsync(CancellationToken cancellationToken) =>
            Task.FromResult<CertificatInstalle?>(new CertificatInstalle("v1", AutoriteEssai.Maintenant.AddDays(2)));
    }

    private sealed class SanteEssai : ISanteGrappe
    {
        public Task<EtatGrappe> EvaluerAsync(ContenuSecretCertificat identite, CancellationToken cancellationToken) =>
            Task.FromResult(new EtatGrappe(true, "3 nœuds"));
    }

    private sealed class CycleDeVieEssai : IHostApplicationLifetime
    {
        public TaskCompletionSource Arret { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() => Arret.TrySetResult();
    }
}

public sealed class ServiceMesuresTests
{
    [Fact]
    public async Task Publie_a_chaque_intervalle_et_survit_a_une_erreur()
    {
        var collecte = new CollecteEssai { EchecsRestants = 1 };
        var publicateur = new PublicateurEssai();
        var surveillance = new SurveillanceGrappe(new SourceEssai(), collecte, publicateur, "kafka-1");
        var configuration = new ConfigurationSentinelle { IntervalleMesures = TimeSpan.FromMilliseconds(5) };
        using var service = new ServiceMesures(surveillance, configuration, TimeProvider.System, NullLogger<ServiceMesures>.Instance);

        await service.StartAsync(TestContext.Current.CancellationToken);
        while (publicateur.Publications < 2)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        await service.StopAsync(TestContext.Current.CancellationToken);
        Assert.True(collecte.Appels >= 3);
    }

    private sealed class SourceEssai : ISourceCertificatCourant
    {
        public Task<CertificatCourant> LireAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new CertificatCourant("v1", new ContenuSecretCertificat("c", "c", "k", "r", AutoriteEssai.Maintenant)));

        public Task<string> LireVersionAsync(CancellationToken cancellationToken) => Task.FromResult("v1");
    }

    private sealed class CollecteEssai : ICollecteMesuresGrappe
    {
        public int EchecsRestants { get; set; }

        public int Appels { get; private set; }

        public Task<MesuresGrappe> CollecterAsync(ContenuSecretCertificat identite, CancellationToken cancellationToken)
        {
            Appels++;
            if (EchecsRestants-- > 0)
            {
                throw new InvalidOperationException("CloudWatch indisponible.");
            }

            return Task.FromResult(new MesuresGrappe(3, 0, 50, new Dictionary<string, long>()));
        }
    }

    private sealed class PublicateurEssai : IPublicateurMesuresGrappe
    {
        public int Publications { get; private set; }

        public Task PublierAsync(string noeud, MesuresGrappe mesures, CancellationToken cancellationToken)
        {
            Publications++;
            return Task.CompletedTask;
        }
    }
}

public sealed class ConfigurationSentinelleTests
{
    [Fact]
    public void Valider_exige_secret_et_serveurs()
    {
        Assert.Throws<InvalidOperationException>(() => new ConfigurationSentinelle().Valider());
        Assert.Throws<InvalidOperationException>(() => new ConfigurationSentinelle { Secret = "arn:secret", Serveurs = "kafka-1:9093" }.Valider());
        new ConfigurationSentinelle { Secret = "arn:secret", Serveurs = "kafka-1:9093", Noeud = "kafka-1" }.Valider();
    }
}
