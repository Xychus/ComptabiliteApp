using System.Text.Json;
using Ledger.Command.Application.EventStore;
using Ledger.Command.Infrastructure.Outbox;
using Ledger.Command.Infrastructure.Postgres;
using Ledger.Command.Infrastructure.Tests.Postgres;

namespace Ledger.Command.Infrastructure.Tests.EventStore;

/// <summary>Suite de contrat contre PostgreSQL (Testcontainers), avec le rôle applicatif.</summary>
[Collection(PostgresGroup.Name)]
[Trait("Category", "Integration")]
public sealed class NpgsqlEventStoreTests(PostgresFixture database) : EventStoreContract
{
    protected override IEventStoreUnderTest Store { get; } = new NpgsqlUnderTest(database);

    private sealed class NpgsqlUnderTest(PostgresFixture database) : IEventStoreUnderTest
    {
        private readonly NpgsqlEventStore _store = new(database.AppDataSource);

        public IEventReader Reader => _store;

        public IPeriodEventReader PeriodReader => _store;

        public TestClock Clock { get; } = new();

        public UnitOfWorkUnderTest BeginUnitOfWork()
        {
            var unitOfWork = new NpgsqlUnitOfWork(database.AppDataSource, Clock);
            return new UnitOfWorkUnderTest(unitOfWork, unitOfWork);
        }

        public async Task<IReadOnlyList<OutboxMessage>> ReadOutboxAsync(IReadOnlyCollection<Guid> eventIds)
        {
            await using var command = database.AppDataSource.CreateCommand(
                "SELECT event_id, topic, partition_key, payload::text FROM ledger.outbox WHERE event_id = ANY(@ids)");
            command.Parameters.AddWithValue("ids", eventIds.ToArray());
            await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            var messages = new List<OutboxMessage>();
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                using var payload = JsonDocument.Parse(reader.GetString(3));
                messages.Add(new OutboxMessage(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), payload.RootElement.Clone()));
            }

            return messages;
        }
    }
}
