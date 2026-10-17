using Ledger.Command.Application.EventStore;
using Ledger.Command.Infrastructure.InMemory;
using Ledger.Command.Infrastructure.Outbox;

namespace Ledger.Command.Infrastructure.Tests.EventStore;

/// <summary>Suite de contrat contre l'event store en mémoire.</summary>
public sealed class InMemoryEventStoreTests : EventStoreContract
{
    protected override IEventStoreUnderTest Store { get; } = new InMemoryUnderTest();

    private sealed class InMemoryUnderTest : IEventStoreUnderTest
    {
        private readonly InMemoryEventStore _store;

        public InMemoryUnderTest() => _store = new InMemoryEventStore(Clock);

        public IEventReader Reader => _store;

        public TestClock Clock { get; } = new();

        public UnitOfWorkUnderTest BeginUnitOfWork()
        {
            var unitOfWork = _store.BeginUnitOfWork();
            return new UnitOfWorkUnderTest(unitOfWork, unitOfWork);
        }

        public Task<IReadOnlyList<OutboxMessage>> ReadOutboxAsync(IReadOnlyCollection<Guid> eventIds) =>
            Task.FromResult<IReadOnlyList<OutboxMessage>>([.. _store.Outbox.Where(m => eventIds.Contains(m.EventId))]);
    }
}
