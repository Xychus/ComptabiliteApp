using Ledger.Command.Application.EventStore;
using Ledger.Command.Infrastructure.Outbox;
using Ledger.SharedKernel;

namespace Ledger.Command.Infrastructure.Tests.EventStore;

/// <summary>Une implémentation de l'event store, vue par la suite de contrat.</summary>
public interface IEventStoreUnderTest
{
    IEventReader Reader { get; }

    TestClock Clock { get; }

    UnitOfWorkUnderTest BeginUnitOfWork();

    Task<IReadOnlyList<OutboxMessage>> ReadOutboxAsync(IReadOnlyCollection<Guid> eventIds);
}

/// <summary>Les deux ports d'une même unité de travail.</summary>
public sealed record UnitOfWorkUnderTest(IEventAppender Appender, IUnitOfWork UnitOfWork)
{
    public Task<Result<IReadOnlyList<StoredEvent>>> CommitAsync(params EventToAppend[] events)
    {
        foreach (var candidate in events)
        {
            Appender.Append(candidate);
        }

        return UnitOfWork.CommitAsync(TestContext.Current.CancellationToken);
    }
}

/// <summary>Horloge réglable ; sa valeur par défaut a une précision plus fine que la microseconde.</summary>
public sealed class TestClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow.AddTicks(7);
}
