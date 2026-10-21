using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Confluent.Kafka;
using Ledger.Contracts.Events;
using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Events;
using Ledger.Projections.Application.Processing;

namespace Ledger.Projections.Infrastructure.Kafka;

/// <summary>Réglages du consommateur Kafka (section <c>Ledger:Kafka</c>).</summary>
public sealed class KafkaSourceOptions
{
    public const string Section = "Ledger:Kafka";

    /// <summary>Serveurs d'amorçage, séparés par des virgules.</summary>
    [Required]
    public string BootstrapServers { get; set; } = string.Empty;

    /// <summary>Sujet publié par l'outbox (ADR 0007).</summary>
    [Required]
    public string Topic { get; set; } = "ledger.events";

    /// <summary>Groupe de consommateurs : clé des checkpoints en base.</summary>
    [Required]
    public string ConsumerGroup { get; set; } = "ledger-projections";

    /// <summary>TLS par défaut (MSK) ; <c>Plaintext</c> seulement en local et en test.</summary>
    public SecurityProtocol SecurityProtocol { get; set; } = SecurityProtocol.Ssl;
}

/// <summary>
/// Consommateur Kafka (Confluent.Kafka). La reprise ne dépend pas des offsets validés dans Kafka :
/// à l'affectation d'une partition, la lecture repart du checkpoint en base, écrit dans la même
/// transaction que les modèles de lecture (ADR 0015). Les offsets sont aussi stockés dans Kafka,
/// après traitement, pour mesurer le retard du groupe.
/// </summary>
public sealed class KafkaEventSource : IEventSource, IDisposable
{
    private readonly KafkaSourceOptions _options;
    private readonly IConsumerCheckpointReader _checkpoints;
    private readonly IConsumer<string?, string?> _consumer;

    public KafkaEventSource(KafkaSourceOptions options, IConsumerCheckpointReader checkpoints)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(checkpoints);
        _options = options;
        _checkpoints = checkpoints;
        var config = new ConsumerConfig
        {
            BootstrapServers = options.BootstrapServers,
            GroupId = options.ConsumerGroup,
            SecurityProtocol = options.SecurityProtocol,
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = true,
            EnableAutoOffsetStore = false,
            IsolationLevel = IsolationLevel.ReadCommitted,
        };
        _consumer = new ConsumerBuilder<string?, string?>(config)
            .SetPartitionsAssignedHandler((_, partitions) => ResumeFromCheckpoints(partitions))
            .Build();
        _consumer.Subscribe(options.Topic);
    }

    public SourceMessage Consume(CancellationToken cancellationToken)
    {
        while (true)
        {
            var result = _consumer.Consume(cancellationToken);

            // Pierre tombale (suppression d'une ligne d'outbox) : aucun événement à projeter.
            if (result.Message.Value is null)
            {
                continue;
            }

            var position = new SourcePosition(_options.ConsumerGroup, result.Topic, result.Partition.Value, result.Offset.Value);
            return new SourceMessage(Parse(result.Message.Value, position), position);
        }
    }

    public void Acknowledge(SourceMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var position = message.Position;
        _consumer.StoreOffset(new TopicPartitionOffset(position.Topic, position.Partition, position.Offset + 1));
    }

    public void Dispose()
    {
        _consumer.Close();
        _consumer.Dispose();
    }

    /// <summary>Enveloppe publiée par l'outbox : JSON en camelCase, montants en chaînes.</summary>
    internal static EventEnvelopeV1 Parse(string value, SourcePosition position)
    {
        try
        {
            return JsonSerializer.Deserialize<EventEnvelopeV1>(value, EventJson.Options)
                ?? throw new EventDecodingException($"Message nul en {position.Topic}/{position.Partition}@{position.Offset}.");
        }
        catch (JsonException exception)
        {
            throw new EventDecodingException($"Enveloppe illisible en {position.Topic}/{position.Partition}@{position.Offset}.", exception);
        }
    }

    private IEnumerable<TopicPartitionOffset> ResumeFromCheckpoints(List<TopicPartition> partitions)
    {
        // Rappel synchrone de librdkafka : la lecture du checkpoint bloque l'affectation, pas la consommation.
        var next = _checkpoints.ReadNextOffsetsAsync(_options.ConsumerGroup, _options.Topic, CancellationToken.None).GetAwaiter().GetResult();
        return partitions.Select(partition => new TopicPartitionOffset(
            partition,
            next.TryGetValue(partition.Partition.Value, out var offset) ? new Offset(offset) : Offset.Beginning));
    }
}
