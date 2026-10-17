using Ledger.Command.Domain.Accounts;
using Ledger.Command.Domain.Entries.Rules;
using Ledger.Command.Domain.Periods;
using Ledger.SharedKernel;

namespace Ledger.Command.Domain.Entries;

public sealed record PostEntry(
    EntryId EntryId,
    EntityId Entity,
    DateOnly BusinessDate,
    string? Description,
    EquatableList<JournalLine> Lines);

/// <summary>Contre-passe <paramref name="EntryId"/> par une écriture inverse identifiée par <paramref name="ReversalEntryId"/>.</summary>
public sealed record ReverseEntry(
    EntryId EntryId,
    EntryId ReversalEntryId,
    DateOnly BusinessDate,
    string? Description);

public sealed record JournalEntryPosted(
    EntryId EntryId,
    EntityId Entity,
    DateOnly BusinessDate,
    PeriodId Period,
    string? Description,
    EquatableList<JournalLine> Lines) : IDomainEvent;

/// <summary>
/// Écriture inverse de <paramref name="ReversalOf"/>, stockée dans le flux de l'écriture d'origine
/// (une seconde contre-passation entre donc en conflit). Ses lignes sont les lignes d'origine inversées.
/// </summary>
public sealed record JournalEntryReversed(
    EntryId ReversalEntryId,
    EntryId ReversalOf,
    EntityId Entity,
    DateOnly BusinessDate,
    PeriodId Period,
    string? Description,
    EquatableList<JournalLine> Lines) : IDomainEvent;

/// <summary>État d'une écriture, reconstruit depuis son flux <c>entry-{entryId}</c>.</summary>
public sealed record JournalEntryState(EntryId EntryId, JournalEntryPosted? Posted, EntryId? ReversedBy)
{
    public static JournalEntryState Initial(EntryId entryId) => new(entryId, null, null);

    public bool IsPosted => Posted is not null;

    public JournalEntryState Evolve(IDomainEvent @event) => @event switch
    {
        JournalEntryPosted posted => this with { Posted = posted },
        JournalEntryReversed reversed => this with { ReversedBy = reversed.ReversalEntryId },
        _ => this,
    };
}

/// <summary>Décisions sur l'agrégat écriture. Les règles de validité sont injectées.</summary>
public sealed class JournalEntryDecider(EntryRuleSet rules)
{
    public static JournalEntryDecider Default { get; } = new(EntryRuleSet.Default);

    /// <param name="state">État courant de l'écriture.</param>
    /// <param name="command">Commande de passation.</param>
    /// <param name="period">État de la période de <c>command.BusinessDate</c>, lu depuis le journal.</param>
    /// <param name="chart">Plan comptable de l'entité.</param>
    public Result<IReadOnlyList<IDomainEvent>> Decide(JournalEntryState state, PostEntry command, PeriodState period, IChartOfAccounts chart)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(period);
        var periodId = PeriodId.FromBusinessDate(command.BusinessDate);
        period.EnsureIs(command.Entity, periodId);

        if (state.IsPosted)
        {
            return Reject(EntryErrors.AlreadyPosted(command.EntryId));
        }

        if (period.RejectionForPosting() is { } periodError)
        {
            return Reject(periodError);
        }

        if (rules.Check(command.Lines, chart) is { } ruleError)
        {
            return Reject(ruleError);
        }

        return Events.Of(new JournalEntryPosted(command.EntryId, command.Entity, command.BusinessDate, periodId, command.Description, command.Lines));
    }

    /// <param name="state">État courant de l'écriture d'origine.</param>
    /// <param name="command">Commande de contre-passation.</param>
    /// <param name="period">État de la période de la date de contre-passation (ADR 0008, H8).</param>
    public static Result<IReadOnlyList<IDomainEvent>> Decide(JournalEntryState state, ReverseEntry command, PeriodState period)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(period);

        if (state.Posted is not { } posted)
        {
            return Reject(EntryErrors.NotFound(command.EntryId));
        }

        if (state.ReversedBy is { } reversedBy)
        {
            return Reject(EntryErrors.AlreadyReversed(command.EntryId, reversedBy));
        }

        if (command.ReversalEntryId == command.EntryId)
        {
            return Reject(EntryErrors.InvalidReversalId(command.EntryId));
        }

        var periodId = PeriodId.FromBusinessDate(command.BusinessDate);
        period.EnsureIs(posted.Entity, periodId);
        if (period.RejectionForPosting() is { } periodError)
        {
            return Reject(periodError);
        }

        return Events.Of(new JournalEntryReversed(
            command.ReversalEntryId,
            command.EntryId,
            posted.Entity,
            command.BusinessDate,
            periodId,
            command.Description,
            EquatableList.Of(posted.Lines.Select(line => line.Inverted()))));
    }

    private static Result<IReadOnlyList<IDomainEvent>> Reject(DomainError error) => Result.Failure<IReadOnlyList<IDomainEvent>>(error);
}
