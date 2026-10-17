using Ledger.SharedKernel;

namespace Ledger.Command.Application.EventStore;

/// <summary>Nombre maximal de tentatives sur conflit de concurrence (ADR 0003).</summary>
public sealed record ConcurrencyRetryPolicy(int MaxAttempts)
{
    public static ConcurrencyRetryPolicy Default { get; } = new(5);

    /// <summary>
    /// Exécute <paramref name="attempt"/> (lecture du journal, décision, validation) et la relance
    /// tant qu'elle échoue sur <see cref="EventStoreErrors.ConcurrencyConflictCode"/>, dans la limite de tentatives.
    /// </summary>
    public async Task<Result<T>> RunAsync<T>(Func<Task<Result<T>>> attempt)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        for (var count = 1; ; count++)
        {
            var outcome = await attempt().ConfigureAwait(false);
            if (outcome.IsSuccess || outcome.Error.Code != EventStoreErrors.ConcurrencyConflictCode || count >= MaxAttempts)
            {
                return outcome;
            }
        }
    }
}
