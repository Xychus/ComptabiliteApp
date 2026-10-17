using Ledger.Command.Application.Events;
using Ledger.Command.Application.EventStore;
using Ledger.Command.Domain;
using Ledger.Command.Domain.Periods;
using Ledger.Contracts.Integrity;
using Ledger.SharedKernel;

namespace Ledger.Command.Application.Periods;

/// <summary>Commande sur une période, avec la version du flux de la période attendue par le client.</summary>
public sealed record PeriodCommand(EntityId Entity, PeriodId Period, long ExpectedVersion);

/// <summary>Accusé d'ouverture ou de scellement : l'événement écrit dans le flux de la période.</summary>
public sealed record PeriodReceipt(
    EntityId Entity,
    PeriodId Period,
    PeriodStatus Status,
    long Version,
    long GlobalSequence,
    DateTimeOffset RecordedAt,
    string Hash,
    PeriodDigest? Digest);

/// <summary>Lecture et écriture du flux d'une période, communes à l'ouverture et au scellement.</summary>
public sealed class PeriodJournal(AggregateStreamReader streams, IEventAppender appender, IUnitOfWork unitOfWork, EventCatalog catalog)
{
    /// <summary>Charge l'état de la période ; rejet <c>WrongExpectedVersion</c> si sa version n'est pas celle attendue.</summary>
    public async Task<Result<StreamSnapshot<PeriodState>>> LoadAsync(PeriodCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        var streamId = StreamIds.Period(command.Entity, command.Period);
        var snapshot = await streams.LoadAsync(streamId, PeriodState.Initial(command.Entity, command.Period), (s, e) => s.Evolve(e), cancellationToken).ConfigureAwait(false);
        return snapshot.Version == command.ExpectedVersion
            ? Result.Success(snapshot)
            : Result.Failure<StreamSnapshot<PeriodState>>(EventStoreErrors.WrongExpectedVersion(streamId, command.ExpectedVersion, snapshot.Version));
    }

    /// <summary>Écrit l'événement décidé à la version suivante du flux (sans date d'affaires).</summary>
    public async Task<Result<PeriodReceipt>> AppendAsync(PeriodCommand command, StreamSnapshot<PeriodState> current, Result<IReadOnlyList<IDomainEvent>> decision, EventMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.IsFailure)
        {
            return Result.Failure<PeriodReceipt>(decision.Error);
        }

        var domainEvent = decision.Value.Single();
        var batch = new PendingBatch(catalog, command.Entity, businessDate: null, command.Period.ToString(), metadata);
        batch.Add(StreamIds.Period(command.Entity, command.Period), current.Version + 1, domainEvent);
        var committed = await batch.CommitAsync(appender, unitOfWork, cancellationToken).ConfigureAwait(false);
        var state = current.State.Evolve(domainEvent);
        return committed.Map(stored => new PeriodReceipt(
            command.Entity,
            command.Period,
            state.Status,
            stored[0].StreamVersion,
            stored[0].GlobalSequence,
            stored[0].RecordedAt,
            stored[0].Hash,
            (domainEvent as PeriodSealed)?.Digest));
    }
}

/// <summary>Ouvre une période (ADR 0008, H5). Pas de nouvelle tentative : la version attendue tranche.</summary>
public sealed class OpenPeriodHandler(PeriodJournal periods)
{
    public async Task<Result<PeriodReceipt>> HandleAsync(PeriodCommand command, EventMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(metadata);
        var current = await periods.LoadAsync(command, cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return Result.Failure<PeriodReceipt>(current.Error);
        }

        var decision = PeriodDecider.Decide(current.Value.State, new OpenPeriod(command.Entity, command.Period));
        return await periods.AppendAsync(command, current.Value, decision, metadata, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Scelle une période avec l'empreinte de ses événements (ADR 0013). Si un événement de la période
/// est validé pendant le calcul, l'event store refuse le sceau et le calcul est refait.
/// </summary>
public sealed class SealPeriodHandler(PeriodJournal periods, IPeriodEventReader periodEvents, ConcurrencyRetryPolicy retryPolicy)
{
    public Task<Result<PeriodReceipt>> HandleAsync(PeriodCommand command, EventMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(metadata);
        return retryPolicy.RunAsync(() => TryOnceAsync(command, metadata, cancellationToken));
    }

    private async Task<Result<PeriodReceipt>> TryOnceAsync(PeriodCommand command, EventMetadata metadata, CancellationToken cancellationToken)
    {
        var current = await periods.LoadAsync(command, cancellationToken).ConfigureAwait(false);
        if (current.IsFailure)
        {
            return Result.Failure<PeriodReceipt>(current.Error);
        }

        var hashes = await periodEvents.ReadPeriodHashesAsync(command.Entity.Value, command.Period.ToString(), cancellationToken).ConfigureAwait(false);
        var digest = new PeriodDigest(hashes.Count, MerkleTree.Root(hashes));
        var decision = PeriodDecider.Decide(current.Value.State, new SealPeriod(command.Entity, command.Period, digest));
        return await periods.AppendAsync(command, current.Value, decision, metadata, cancellationToken).ConfigureAwait(false);
    }
}
