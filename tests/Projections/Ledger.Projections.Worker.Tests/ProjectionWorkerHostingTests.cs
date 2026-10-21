using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Processing;
using Ledger.Projections.Application.Rebuild;
using Ledger.Projections.Application.Taxes;
using Ledger.Projections.Infrastructure.InMemory;
using Ledger.Projections.Tests.Support;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Ledger.Projections.Worker.Tests;

/// <summary>Composition du worker : fournisseurs, configuration fiscale, modes consommation et rejeu.</summary>
public sealed class ProjectionWorkerHostingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Les_comptes_fiscaux_sont_lus_par_entite_dans_la_configuration()
    {
        var builder = Builder(Environments.Development, new()
        {
            [ProjectionWorkerHosting.ProviderKey] = ProjectionWorkerHosting.InMemoryProvider,
            ["Ledger:Tax:Entities:ACME:GstCollected:0"] = "2310",
            ["Ledger:Tax:Entities:ACME:GstPaid:0"] = "1350",
            ["Ledger:Tax:Entities:ACME:QstCollected:0"] = "2320",
            ["Ledger:Tax:Entities:ACME:QstPaid:0"] = "1360",
            ["Ledger:Tax:Entities:ACME:QstPaid:1"] = "1361",
        });
        builder.AddProjectionWorker(consume: false);
        using var host = builder.Build();

        var map = host.Services.GetRequiredService<TaxAccountMap>();

        Assert.Equal(TaxKind.GstCollected, map.Find("ACME", "2310"));
        Assert.Equal(TaxKind.GstPaid, map.Find("ACME", "1350"));
        Assert.Equal(TaxKind.QstCollected, map.Find("ACME", "2320"));
        Assert.Equal(TaxKind.QstPaid, map.Find("ACME", "1361"));
        Assert.Null(map.Find("AUTRE", "2310"));
    }

    [Fact]
    public void Sans_configuration_fiscale_aucun_compte_n_est_fiscal()
    {
        var builder = Builder(Environments.Development, new() { [ProjectionWorkerHosting.ProviderKey] = ProjectionWorkerHosting.InMemoryProvider });
        builder.AddProjectionWorker(consume: false);
        using var host = builder.Build();

        Assert.Null(host.Services.GetRequiredService<TaxAccountMap>().Find("ACME", "2310"));
    }

    [Fact]
    public void Les_projections_en_memoire_sont_reservees_au_developpement()
    {
        var builder = Builder(Environments.Production, new() { [ProjectionWorkerHosting.ProviderKey] = ProjectionWorkerHosting.InMemoryProvider });

        Assert.Throws<InvalidOperationException>(() => builder.AddProjectionWorker(consume: false));
    }

    [Fact]
    public void PostgreSQL_exige_une_chaine_de_connexion()
    {
        var builder = Builder(Environments.Production, []);

        var error = Assert.Throws<InvalidOperationException>(() => builder.AddProjectionWorker(consume: false));

        Assert.Contains(ProjectionWorkerHosting.ConnectionStringName, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task La_consommation_exige_les_serveurs_Kafka_au_demarrage()
    {
        var builder = Builder(Environments.Production, new() { ["ConnectionStrings:Projections"] = "Host=localhost;Username=ledger_projection" });
        builder.AddProjectionWorker(consume: true);
        using var host = builder.Build();

        await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(Ct));
    }

    [Fact]
    public async Task Le_rejeu_d_une_projection_connue_reussit_et_une_inconnue_est_refusee()
    {
        var builder = Builder(Environments.Development, new() { [ProjectionWorkerHosting.ProviderKey] = ProjectionWorkerHosting.InMemoryProvider });
        builder.AddProjectionWorker(consume: false);
        using var host = builder.Build();

        Assert.NotNull(host.Services.GetRequiredService<ProjectionRebuilder>());
        Assert.Equal(0, await ProjectionWorkerHosting.RunRebuildAsync(host, ProjectionNames.AccountBalances, Ct));
        Assert.Equal(2, await ProjectionWorkerHosting.RunRebuildAsync(host, "inconnue", Ct));
        Assert.Empty(host.Services.GetServices<IHostedService>().OfType<ProjectionConsumerService>());
    }

    [Fact]
    public async Task Le_service_de_consommation_fait_tourner_la_boucle_jusqu_a_l_arret()
    {
        var builder = Builder(Environments.Development, new() { [ProjectionWorkerHosting.ProviderKey] = ProjectionWorkerHosting.InMemoryProvider });
        builder.AddProjectionWorker(consume: false);
        using var host = builder.Build();
        var source = new OneMessageSource();
        using var service = new ProjectionConsumerService(host.Services.GetRequiredService<ProjectionPump>(), source);

        await service.StartAsync(Ct);
        await source.Consumed.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await service.StopAsync(Ct);

        Assert.Single(host.Services.GetRequiredService<InMemoryProjectionDatabase>().Rows<ConsumerOffsetKey, long>("consumer_offsets"));
        Assert.Single(source.Acknowledged);
    }

    private static HostApplicationBuilder Builder(string environment, Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment, DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(settings);
        return builder;
    }

    /// <summary>Un message, puis attente de l'annulation (comme Kafka sans nouveau message).</summary>
    private sealed class OneMessageSource : IEventSource
    {
        private EventEnvelopeV1? _pending = new JournalBuilder("ACME").Opened("2026-03");

        public TaskCompletionSource Consumed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<SourceMessage> Acknowledged { get; } = [];

        public SourceMessage Consume(CancellationToken cancellationToken)
        {
            if (_pending is { } envelope)
            {
                _pending = null;
                return new SourceMessage(envelope, new SourcePosition("tests", "ledger.events", 0, 0));
            }

            cancellationToken.WaitHandle.WaitOne();
            throw new OperationCanceledException(cancellationToken);
        }

        public void Acknowledge(SourceMessage message)
        {
            Acknowledged.Add(message);
            Consumed.TrySetResult();
        }
    }
}
