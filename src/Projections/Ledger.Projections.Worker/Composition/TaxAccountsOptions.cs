using Ledger.Projections.Application.Taxes;

namespace Ledger.Projections.Worker.Composition;

/// <summary>
/// Comptes fiscaux par entité (section <c>Ledger:Tax</c>, ADR 0018), par exemple :
/// <c>Ledger:Tax:Entities:ACME:GstCollected:0 = 2310</c>. Changer cette configuration exige
/// un rejeu de <c>tax_projection</c>.
/// </summary>
public sealed class TaxAccountsOptions
{
    public const string Section = "Ledger:Tax";

    public Dictionary<string, EntityTaxAccounts> Entities { get; init; } = new(StringComparer.Ordinal);

    public TaxAccountMap ToMap() => new(Entities.SelectMany(entity => entity.Value.Accounts(entity.Key)));
}

/// <summary>Comptes de TPS et de TVQ d'une entité, perçues et payées.</summary>
public sealed class EntityTaxAccounts
{
    public IReadOnlyList<string> GstCollected { get; init; } = [];

    public IReadOnlyList<string> GstPaid { get; init; } = [];

    public IReadOnlyList<string> QstCollected { get; init; } = [];

    public IReadOnlyList<string> QstPaid { get; init; } = [];

    internal IEnumerable<TaxAccount> Accounts(string entityId) =>
        GstCollected.Select(account => new TaxAccount(entityId, account, TaxKind.GstCollected))
            .Concat(GstPaid.Select(account => new TaxAccount(entityId, account, TaxKind.GstPaid)))
            .Concat(QstCollected.Select(account => new TaxAccount(entityId, account, TaxKind.QstCollected)))
            .Concat(QstPaid.Select(account => new TaxAccount(entityId, account, TaxKind.QstPaid)));
}
