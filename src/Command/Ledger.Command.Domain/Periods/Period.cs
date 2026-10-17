using Ledger.SharedKernel;

namespace Ledger.Command.Domain.Periods;

public enum PeriodStatus
{
    NotOpened = 0,
    Open = 1,
    Sealed = 2,
}

public sealed record OpenPeriod(EntityId Entity, PeriodId Period);

public sealed record SealPeriod(EntityId Entity, PeriodId Period);

public sealed record PeriodOpened(EntityId Entity, PeriodId Period) : IDomainEvent;

public sealed record PeriodSealed(EntityId Entity, PeriodId Period) : IDomainEvent;

/// <summary>État d'une période d'une entité, reconstruit depuis son flux d'événements.</summary>
public sealed record PeriodState(EntityId Entity, PeriodId Period, PeriodStatus Status)
{
    public static PeriodState Initial(EntityId entity, PeriodId period) => new(entity, period, PeriodStatus.NotOpened);

    public PeriodState Evolve(IDomainEvent @event) => @event switch
    {
        PeriodOpened => this with { Status = PeriodStatus.Open },
        PeriodSealed => this with { Status = PeriodStatus.Sealed },
        _ => this,
    };

    /// <summary>
    /// Vérifie qu'une écriture peut être passée dans cette période (ADR 0008, H5).
    /// Renvoie <c>null</c> si c'est le cas.
    /// </summary>
    public DomainError? RejectionForPosting() => Status switch
    {
        PeriodStatus.Open => null,
        PeriodStatus.Sealed => PeriodErrors.Sealed(Entity, Period),
        _ => PeriodErrors.NotOpen(Entity, Period),
    };

    internal void EnsureIs(EntityId entity, PeriodId period)
    {
        if (Entity != entity || Period != period)
        {
            throw new ArgumentException($"État de période {Entity}/{Period} fourni pour {entity}/{period}.");
        }
    }
}

/// <summary>Décisions sur l'agrégat période.</summary>
public static class PeriodDecider
{
    public static Result<IReadOnlyList<IDomainEvent>> Decide(PeriodState state, OpenPeriod command)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        state.EnsureIs(command.Entity, command.Period);

        return state.Status switch
        {
            PeriodStatus.NotOpened => Events.Of(new PeriodOpened(command.Entity, command.Period)),
            PeriodStatus.Open => Result.Failure<IReadOnlyList<IDomainEvent>>(PeriodErrors.AlreadyOpen(command.Entity, command.Period)),
            _ => Result.Failure<IReadOnlyList<IDomainEvent>>(PeriodErrors.Sealed(command.Entity, command.Period)),
        };
    }

    public static Result<IReadOnlyList<IDomainEvent>> Decide(PeriodState state, SealPeriod command)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        state.EnsureIs(command.Entity, command.Period);

        return state.Status switch
        {
            PeriodStatus.Open => Events.Of(new PeriodSealed(command.Entity, command.Period)),
            PeriodStatus.Sealed => Result.Failure<IReadOnlyList<IDomainEvent>>(PeriodErrors.AlreadySealed(command.Entity, command.Period)),
            _ => Result.Failure<IReadOnlyList<IDomainEvent>>(PeriodErrors.NotOpen(command.Entity, command.Period)),
        };
    }
}

public static class PeriodErrors
{
    public const string NotOpenCode = "PeriodNotOpen";
    public const string SealedCode = "PeriodSealed";
    public const string AlreadyOpenCode = "PeriodAlreadyOpen";
    public const string AlreadySealedCode = "PeriodAlreadySealed";

    public static DomainError NotOpen(EntityId entity, PeriodId period) =>
        DomainError.WithDetails(NotOpenCode, $"La période {period} n'est pas ouverte.", Details(entity, period));

    public static DomainError Sealed(EntityId entity, PeriodId period) =>
        DomainError.WithDetails(SealedCode, $"La période {period} est scellée.", Details(entity, period));

    public static DomainError AlreadyOpen(EntityId entity, PeriodId period) =>
        DomainError.WithDetails(AlreadyOpenCode, $"La période {period} est déjà ouverte.", Details(entity, period));

    public static DomainError AlreadySealed(EntityId entity, PeriodId period) =>
        DomainError.WithDetails(AlreadySealedCode, $"La période {period} est déjà scellée.", Details(entity, period));

    private static (string, string)[] Details(EntityId entity, PeriodId period) =>
        [("entity", entity.Value), ("period", period.ToString())];
}
