using Ledger.Command.Application.Events;
using Ledger.Command.Application.EventStore;
using Ledger.Command.Domain.Accounts;
using Ledger.Command.Domain.Periods;
using Ledger.SharedKernel;

namespace Ledger.Command.Application.Accounts;

/// <summary>
/// Accusé de définition d'un compte : l'événement <c>AccountDefined</c> qui porte son état courant.
/// <see cref="Created"/> est vrai si le compte n'existait pas.
/// </summary>
public sealed record AccountReceipt(EntityId Entity, AccountId Account, long ChartVersion, long GlobalSequence, DateTimeOffset RecordedAt, string Hash, bool Created);

/// <summary>
/// Définit ou modifie un compte dans le flux <c>chart-{entité}</c> (ADR 0012). Une définition
/// identique ne produit aucun événement et renvoie l'événement existant ; un conflit de version
/// (autre compte défini en même temps) relance lecture et décision.
/// </summary>
public sealed class DefineAccountHandler(
    AggregateStreamReader streams,
    IEventAppender appender,
    IUnitOfWork unitOfWork,
    EventCatalog catalog,
    IClock clock,
    ConcurrencyRetryPolicy retryPolicy)
{
    public Task<Result<AccountReceipt>> HandleAsync(DefineAccount command, EventMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(metadata);
        return retryPolicy.RunAsync(() => TryOnceAsync(command, metadata, cancellationToken));
    }

    private async Task<Result<AccountReceipt>> TryOnceAsync(DefineAccount command, EventMetadata metadata, CancellationToken cancellationToken)
    {
        var streamId = StreamIds.Chart(command.Entity);
        var chart = await streams.LoadAsync(streamId, ChartState.Initial(command.Entity), (s, e) => s.Evolve(e), cancellationToken).ConfigureAwait(false);
        var decision = ChartDecider.Decide(chart.State, command);
        if (decision.IsFailure)
        {
            return Result.Failure<AccountReceipt>(decision.Error);
        }

        if (decision.Value.Count == 0)
        {
            var current = chart.Events.Last(e => Defines(e, command.Account));
            return Result.Success(new AccountReceipt(command.Entity, command.Account, chart.Version, current.GlobalSequence, current.RecordedAt, current.Hash, Created: false));
        }

        // Pas de date d'affaires : la période est celle de la date UTC du serveur (ADR 0012).
        var period = PeriodId.FromBusinessDate(DateOnly.FromDateTime(clock.UtcNow.UtcDateTime));
        var batch = new PendingBatch(catalog, command.Entity, businessDate: null, period.ToString(), metadata);
        var version = chart.Version;
        foreach (var domainEvent in decision.Value)
        {
            batch.Add(streamId, ++version, domainEvent);
        }

        var created = !chart.State.Accounts.ContainsKey(command.Account);
        var committed = await batch.CommitAsync(appender, unitOfWork, cancellationToken).ConfigureAwait(false);
        return committed.Map(stored => new AccountReceipt(command.Entity, command.Account, stored[^1].StreamVersion, stored[^1].GlobalSequence, stored[^1].RecordedAt, stored[^1].Hash, created));
    }

    private bool Defines(StoredEvent stored, AccountId account) =>
        catalog.Deserialize(stored.EventType, stored.SchemaVersion, stored.Payload) is AccountDefined defined && defined.Account == account;
}
