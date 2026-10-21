using Ledger.Projections.Application.Processing;
using Npgsql;

namespace Ledger.Projections.Infrastructure.Postgres;

/// <summary>Positions validées du consommateur (<c>consumer_offsets</c>), lues à l'affectation des partitions.</summary>
public sealed class NpgsqlConsumerCheckpointReader(NpgsqlDataSource dataSource) : IConsumerCheckpointReader
{
    private const string ReadSql =
        "SELECT partition, next_offset FROM projections.consumer_offsets WHERE consumer_group = @consumer_group AND topic = @topic";

    public async Task<IReadOnlyDictionary<int, long>> ReadNextOffsetsAsync(string consumerGroup, string topic, CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand(ReadSql);
        await using (command.ConfigureAwait(false))
        {
            command.Parameters.AddWithValue("consumer_group", consumerGroup);
            command.Parameters.AddWithValue("topic", topic);
            var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                var offsets = new Dictionary<int, long>();
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    offsets[reader.GetInt32(0)] = reader.GetInt64(1);
                }

                return offsets;
            }
        }
    }
}
