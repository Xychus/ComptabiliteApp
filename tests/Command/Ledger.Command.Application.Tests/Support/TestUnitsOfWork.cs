using Ledger.Command.Application.EventStore;
using Ledger.Command.Infrastructure.InMemory;
using Ledger.SharedKernel;

namespace Ledger.Command.Application.Tests.Support;

/// <summary>Compte les validations.</summary>
internal sealed class CountingUnitOfWork(InMemoryUnitOfWork inner) : IEventAppender, IUnitOfWork
{
    public int Commits { get; private set; }

    public void Append(EventToAppend eventToAppend) => inner.Append(eventToAppend);

    public Task<Result<IReadOnlyList<StoredEvent>>> CommitAsync(CancellationToken cancellationToken)
    {
        Commits++;
        return inner.CommitAsync(cancellationToken);
    }
}

/// <summary>Fait valider une commande concurrente juste avant la première validation.</summary>
internal sealed class RacingUnitOfWork(InMemoryUnitOfWork inner, Func<Task> competitor) : IEventAppender, IUnitOfWork
{
    public int Commits { get; private set; }

    public void Append(EventToAppend eventToAppend) => inner.Append(eventToAppend);

    public async Task<Result<IReadOnlyList<StoredEvent>>> CommitAsync(CancellationToken cancellationToken)
    {
        if (++Commits == 1)
        {
            await competitor();
        }

        return await inner.CommitAsync(cancellationToken);
    }
}

/// <summary>Refuse toute validation pour conflit de concurrence.</summary>
internal sealed class AlwaysConflictingUnitOfWork : IEventAppender, IUnitOfWork
{
    public int Commits { get; private set; }

    public void Append(EventToAppend eventToAppend)
    {
    }

    public Task<Result<IReadOnlyList<StoredEvent>>> CommitAsync(CancellationToken cancellationToken)
    {
        Commits++;
        return Task.FromResult(Result.Failure<IReadOnlyList<StoredEvent>>(EventStoreErrors.ConcurrencyConflict()));
    }
}
