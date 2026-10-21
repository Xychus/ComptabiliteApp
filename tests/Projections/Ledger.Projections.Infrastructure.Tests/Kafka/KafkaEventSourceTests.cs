using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using Ledger.Contracts.Events;
using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Events;
using Ledger.Projections.Application.Processing;
using Ledger.Projections.Infrastructure.Kafka;
using Ledger.Projections.Infrastructure.Tests.Contract;
using Ledger.Projections.Infrastructure.Tests.Postgres;
using Ledger.Projections.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.Kafka;
using static Ledger.Projections.Tests.Support.JournalBuilder;

namespace Ledger.Projections.Infrastructure.Tests.Kafka;

/// <summary>Kafka éphémère (Testcontainers), partagé par la classe de tests.</summary>
public sealed class KafkaFixture : IAsyncLifetime
{
    public const string Image = "confluentinc/cp-kafka:7.9.2";

    private readonly KafkaContainer _container = new KafkaBuilder(Image).Build();

    public string BootstrapServers => _container.GetBootstrapAddress();

    public ValueTask InitializeAsync() => new(_container.StartAsync());

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}

/// <summary>
/// Consommation Kafka de bout en bout vers PostgreSQL : la reprise vient du checkpoint en base,
/// une relivraison n'a aucun effet (ADR 0015).
/// </summary>
[Collection(PostgresGroup.Name)]
[Trait("Category", "Integration")]
public sealed class KafkaEventSourceTests(PostgresFixture database, KafkaFixture kafka) : IClassFixture<KafkaFixture>
{
    private static readonly TimeSpan _timeout = TimeSpan.FromSeconds(60);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Le_worker_projette_les_messages_puis_reprend_au_checkpoint_en_base()
    {
        await using var backend = new NpgsqlProjectionStoreTests.NpgsqlBackend(database);
        var topic = await CreateTopicAsync();
        var stored = await backend.RecordAllAsync(Scenario.Build(backend.Entity, backend.OtherEntity));
        await ProduceAsync(topic, stored);

        await ConsumeUntilAsync(backend, topic, expectedMessages: stored.Count);
        var firstRun = await backend.DumpAsync(ProjectionNames.AccountBalances, backend.Entity);
        await backend.RebuildAsync(ProjectionNames.AccountBalances);
        Assert.Equal(firstRun, await backend.DumpAsync(ProjectionNames.AccountBalances, backend.Entity));
        var cash = await backend.BalanceAsync(backend.Entity, "1000");

        // Deuxième exécution : un doublon (republication) et un nouvel événement.
        var journal = new JournalBuilder(backend.Entity);
        var added = await backend.RecordAllAsync([journal.Posted(new DateOnly(2026, 4, 2), Debit("1000", 3m), Credit("4000", 3m))]);
        await ProduceAsync(topic, [stored[6], added[0]]);

        await ConsumeUntilAsync(backend, topic, expectedMessages: stored.Count + 2);

        Assert.Equal((cash!.Value.Debit + 3m, cash.Value.Credit), await backend.BalanceAsync(backend.Entity, "1000"));
    }

    [Fact]
    public async Task Une_pierre_tombale_est_ignoree_et_un_message_illisible_est_une_erreur()
    {
        await using var backend = new NpgsqlProjectionStoreTests.NpgsqlBackend(database);
        var topic = await CreateTopicAsync(partitions: 1);
        var valid = new JournalBuilder(backend.Entity).Opened("2026-03");
        using (var producer = new ProducerBuilder<string, string?>(new ProducerConfig { BootstrapServers = kafka.BootstrapServers }).Build())
        {
            await producer.ProduceAsync(topic, new Message<string, string?> { Key = backend.Entity, Value = null }, Ct);
            await producer.ProduceAsync(topic, new Message<string, string?> { Key = backend.Entity, Value = Serialize(valid) }, Ct);
            await producer.ProduceAsync(topic, new Message<string, string?> { Key = backend.Entity, Value = "{pas du json" }, Ct);
        }

        using var source = CreateSource(backend, topic);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(_timeout);

        var message = source.Consume(timeout.Token);
        Assert.Equal(valid.EventId, message.Envelope.EventId);
        Assert.Equal(new SourcePosition(backend.ConsumerGroup, topic, 0, 1), message.Position);
        source.Acknowledge(message);
        Assert.Throws<EventDecodingException>(() => source.Consume(timeout.Token));
    }

    private KafkaEventSource CreateSource(ProjectionBackend backend, string topic) =>
        new(
            new KafkaSourceOptions { BootstrapServers = kafka.BootstrapServers, Topic = topic, ConsumerGroup = backend.ConsumerGroup, SecurityProtocol = SecurityProtocol.Plaintext },
            backend.Services.GetRequiredService<IConsumerCheckpointReader>());

    /// <summary>Lance la boucle du worker jusqu'à ce que le checkpoint en base couvre <paramref name="expectedMessages"/> messages.</summary>
    private async Task ConsumeUntilAsync(ProjectionBackend backend, string topic, int expectedMessages)
    {
        using var source = CreateSource(backend, topic);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var pump = backend.Services.GetRequiredService<ProjectionPump>();
        var running = Task.Run(() => pump.RunAsync(source, stop.Token), Ct);
        var checkpoints = backend.Services.GetRequiredService<IConsumerCheckpointReader>();
        var deadline = DateTimeOffset.UtcNow + _timeout;
        while ((await checkpoints.ReadNextOffsetsAsync(backend.ConsumerGroup, topic, Ct)).Values.Sum() < expectedMessages)
        {
            Assert.False(running.IsCompleted, $"La boucle s'est arrêtée : {running.Exception}");
            Assert.True(DateTimeOffset.UtcNow < deadline, "Messages non projetés à temps.");
            await Task.Delay(TimeSpan.FromMilliseconds(200), Ct);
        }

        await stop.CancelAsync();
        await running;
    }

    private async Task<string> CreateTopicAsync(int partitions = 3)
    {
        var topic = "ledger.events." + Guid.CreateVersion7().ToString("N");
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = kafka.BootstrapServers }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = topic, NumPartitions = partitions, ReplicationFactor = 1 }]);
        return topic;
    }

    /// <summary>Publie comme le routeur d'outbox de Debezium : clé = entité, valeur = enveloppe JSON (ADR 0007).</summary>
    private async Task ProduceAsync(string topic, IEnumerable<EventEnvelopeV1> events)
    {
        using var producer = new ProducerBuilder<string, string>(new ProducerConfig { BootstrapServers = kafka.BootstrapServers, EnableIdempotence = true }).Build();
        foreach (var envelope in events)
        {
            await producer.ProduceAsync(topic, new Message<string, string> { Key = envelope.EntityId, Value = Serialize(envelope) }, Ct);
        }
    }

    private static string Serialize(EventEnvelopeV1 envelope) => JsonSerializer.Serialize(envelope, EventJson.Options);
}
