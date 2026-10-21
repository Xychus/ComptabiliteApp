using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Entries;
using Ledger.Projections.Application.Events;
using Ledger.Projections.Application.Processing;

namespace Ledger.Projections.Application.Taxes;

/// <summary>Rôle fiscal d'un compte (ADR 0018).</summary>
public enum TaxKind
{
    /// <summary>TPS à percevoir (compte de passif, sens normal crédit).</summary>
    GstCollected = 0,

    /// <summary>TPS payée : crédits de taxe sur les intrants, CTI (compte d'actif, sens normal débit).</summary>
    GstPaid = 1,

    /// <summary>TVQ à percevoir.</summary>
    QstCollected = 2,

    /// <summary>TVQ payée : remboursements de taxe sur les intrants, RTI.</summary>
    QstPaid = 3,
}

/// <summary>Montant de taxe d'une période ; positif dans le sens normal du compte fiscal.</summary>
public sealed record TaxMovement(string EntityId, string Period, string Currency, TaxKind Kind, decimal Amount);

/// <summary>Port d'écriture de <c>tax_projection</c> : totaux par entité, période, devise et rôle fiscal.</summary>
public interface ITaxProjectionStore
{
    Task AddAsync(TaxMovement movement, CancellationToken cancellationToken);
}

/// <summary>
/// Comptes fiscaux de chaque entité, issus de la configuration du worker (ADR 0018). Les
/// événements ne portent pas de rôle fiscal : changer cette table exige un rejeu de
/// <c>tax_projection</c>.
/// </summary>
public sealed class TaxAccountMap
{
    private readonly Dictionary<(string Entity, string Account), TaxKind> _kinds = [];

    public TaxAccountMap(IEnumerable<TaxAccount> accounts)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        foreach (var account in accounts)
        {
            if (!_kinds.TryAdd((account.EntityId, account.AccountId), account.Kind))
            {
                throw new ArgumentException($"Le compte {account.AccountId} de l'entité {account.EntityId} a plusieurs rôles fiscaux.", nameof(accounts));
            }
        }
    }

    public static TaxAccountMap Empty { get; } = new([]);

    public TaxKind? Find(string entityId, string accountId) =>
        _kinds.TryGetValue((entityId, accountId), out var kind) ? kind : null;
}

/// <summary>Compte fiscal d'une entité.</summary>
public sealed record TaxAccount(string EntityId, string AccountId, TaxKind Kind);

/// <summary>
/// TPS/TVQ perçues et payées par période (base de la déclaration). Une écriture compte dans la
/// période de sa date d'affaires ; une contre-passation, dans la sienne (ADR 0008, H8). Le
/// montant est mesuré dans le sens normal du compte : crédits − débits pour les taxes perçues,
/// débits − crédits pour les taxes payées.
/// </summary>
public sealed class TaxProjection(TaxAccountMap accounts, ITaxProjectionStore store)
    : IProjection, IEventHandler<JournalEntryPostedV1>, IEventHandler<JournalEntryReversedV1>
{
    public string Name => ProjectionNames.TaxProjection;

    public Task HandleAsync(ProjectedEvent<JournalEntryPostedV1> projectedEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectedEvent);
        return AddAsync(projectedEvent.Data.EntityId, projectedEvent.Data.Period, projectedEvent.Data.Lines, cancellationToken);
    }

    public Task HandleAsync(ProjectedEvent<JournalEntryReversedV1> projectedEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projectedEvent);
        return AddAsync(projectedEvent.Data.EntityId, projectedEvent.Data.Period, projectedEvent.Data.Lines, cancellationToken);
    }

    private async Task AddAsync(string entityId, string period, IReadOnlyList<JournalLineV1> lines, CancellationToken cancellationToken)
    {
        foreach (var movement in JournalLines.Movements(lines))
        {
            if (accounts.Find(entityId, movement.AccountId) is not { } kind)
            {
                continue;
            }

            var amount = kind is TaxKind.GstCollected or TaxKind.QstCollected
                ? movement.Credit - movement.Debit
                : movement.Debit - movement.Credit;
            await store.AddAsync(new TaxMovement(entityId, period, movement.Currency, kind, amount), cancellationToken).ConfigureAwait(false);
        }
    }
}
