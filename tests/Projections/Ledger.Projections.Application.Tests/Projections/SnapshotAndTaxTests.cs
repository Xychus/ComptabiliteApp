using Ledger.Projections.Application.Processing;
using Ledger.Projections.Application.Taxes;
using Ledger.Projections.Application.Tests.Support;
using Ledger.Projections.Infrastructure;
using Ledger.Projections.Infrastructure.InMemory;
using Ledger.Projections.Tests.Support;
using static Ledger.Projections.Tests.Support.JournalBuilder;

namespace Ledger.Projections.Application.Tests.Projections;

/// <summary>Instantanés de période (ADR 0017) et projection TPS/TVQ (ADR 0018).</summary>
public sealed class SnapshotAndTaxTests : IAsyncDisposable
{
    private const string Receivables = "1100";
    private const string Sales = "4000";
    private const string GstPayable = "2310";
    private const string QstPayable = "2320";
    private const string GstItc = "1350";
    private const string QstItr = "1360";
    private const string Supplies = "5100";
    private const string Payables = "2100";

    private static readonly DateOnly _march2 = new(2026, 3, 2);
    private static readonly DateOnly _march20 = new(2026, 3, 20);
    private static readonly DateOnly _april3 = new(2026, 4, 3);

    private readonly InMemoryProjections _projections = new(new TaxAccountMap(
    [
        new TaxAccount("ACME", GstPayable, TaxKind.GstCollected),
        new TaxAccount("ACME", GstItc, TaxKind.GstPaid),
        new TaxAccount("ACME", QstPayable, TaxKind.QstCollected),
        new TaxAccount("ACME", QstItr, TaxKind.QstPaid),
    ]));

    private readonly JournalBuilder _acme = new("ACME");

    public ValueTask DisposeAsync() => _projections.DisposeAsync();

    /// <summary>Exemple de référence (ADR 0008) : vente de 100 $ et achat de 50 $, taxables.</summary>
    [Fact]
    public async Task Exemple_de_reference_TPS_TVQ_percues_et_payees()
    {
        await _projections.DeliverAsync(
            _acme.Posted(_march2, Debit(Receivables, 114.98m), Credit(Sales, 100m), Credit(GstPayable, 5m), Credit(QstPayable, 9.98m)),
            _acme.Posted(_march20, Debit(Supplies, 50m), Debit(GstItc, 2.50m), Debit(QstItr, 4.99m), Credit(Payables, 57.49m)));

        var taxes = _projections.Rows<TaxKey, TaxRow>(ProjectionTables.TaxProjection);

        var march = Assert.Single(taxes);
        Assert.Equal(new TaxKey("ACME", "2026-03", Currency), march.Key);
        Assert.Equal(new TaxRow(GstCollected: 5.00m, GstPaid: 2.50m, QstCollected: 9.98m, QstPaid: 4.99m), march.Value);
    }

    [Fact]
    public async Task Une_contre_passation_reduit_la_taxe_de_sa_propre_periode()
    {
        var entryId = Guid.CreateVersion7();
        await _projections.DeliverAsync(
            _acme.Posted(entryId, _march2, Debit(Receivables, 114.98m), Credit(Sales, 100m), Credit(GstPayable, 5m), Credit(QstPayable, 9.98m)),
            _acme.Reversed(entryId, _april3));

        var taxes = _projections.Rows<TaxKey, TaxRow>(ProjectionTables.TaxProjection);

        Assert.Equal(new TaxRow(5.00m, 0m, 9.98m, 0m), taxes[new TaxKey("ACME", "2026-03", Currency)]);
        Assert.Equal(new TaxRow(-5.00m, 0m, -9.98m, 0m), taxes[new TaxKey("ACME", "2026-04", Currency)]);
    }

    [Fact]
    public async Task Les_comptes_non_fiscaux_et_les_autres_entites_sont_ignores()
    {
        var other = new JournalBuilder("AUTRE");
        await _projections.DeliverAsync(
            _acme.Posted(_march2, Debit(Receivables, 100m), Credit(Sales, 100m)),
            other.Posted(_march2, Debit(Receivables, 105m), Credit(Sales, 100m), Credit(GstPayable, 5m)));

        Assert.Empty(_projections.Rows<TaxKey, TaxRow>(ProjectionTables.TaxProjection));
    }

    [Fact]
    public void Un_compte_ne_peut_avoir_deux_roles_fiscaux() =>
        Assert.Throws<ArgumentException>(() => new TaxAccountMap([new TaxAccount("ACME", GstPayable, TaxKind.GstCollected), new TaxAccount("ACME", GstPayable, TaxKind.QstCollected)]));

    [Fact]
    public async Task L_instantane_fige_les_mouvements_de_la_periode_scellee()
    {
        var opened = _acme.Opened("2026-03");
        var sale = _acme.Posted(_march2, Debit(Receivables, 114.98m), Credit(Sales, 100m), Credit(GstPayable, 5m), Credit(QstPayable, 9.98m));
        var other = _acme.Posted(_march20, Debit(Receivables, 10m), Credit(Sales, 10m));
        var sealedEvent = _acme.Sealed("2026-03");
        await _projections.DeliverAsync(opened, sale, other, sealedEvent);

        var snapshot = _projections.Rows<SnapshotKey, SnapshotRow>(ProjectionTables.PeriodSnapshots);

        Assert.Equal(4, snapshot.Count);
        Assert.Equal(new SnapshotRow(124.98m, 0m, sealedEvent.GlobalSequence, 3), snapshot[new SnapshotKey("ACME", "2026-03", Receivables, Currency)]);
        Assert.Equal(new SnapshotRow(0m, 110m, sealedEvent.GlobalSequence, 3), snapshot[new SnapshotKey("ACME", "2026-03", Sales, Currency)]);
        Assert.Equal(snapshot.Values.Sum(row => row.Debits), snapshot.Values.Sum(row => row.Credits));
    }

    [Fact]
    public async Task L_instantane_ne_depend_pas_du_retard_des_projections()
    {
        // Une écriture validée avant le sceau, mais livrée après lui (autre lot, retard du worker) :
        // l'instantané se calcule depuis le journal, il la contient quand même.
        var late = _acme.Posted(_march20, Debit(Receivables, 10m), Credit(Sales, 10m));
        var sealedEvent = _acme.Sealed("2026-03");
        _projections.Record(late);

        await _projections.DeliverAsync(sealedEvent);
        await _projections.RedeliverAsync(late);

        var snapshot = _projections.Rows<SnapshotKey, SnapshotRow>(ProjectionTables.PeriodSnapshots);
        Assert.Equal(10m, snapshot[new SnapshotKey("ACME", "2026-03", Receivables, Currency)].Debits);
    }

    [Fact]
    public async Task Les_ecritures_des_autres_periodes_et_entites_ne_sont_pas_dans_l_instantane()
    {
        var other = new JournalBuilder("AUTRE");
        var entryId = Guid.CreateVersion7();
        await _projections.DeliverAsync(
            _acme.Posted(entryId, _march2, Debit(Receivables, 50m), Credit(Sales, 50m)),
            _acme.Posted(_april3, Debit(Receivables, 70m), Credit(Sales, 70m)),
            _acme.Reversed(entryId, _march20),
            other.Posted(_march2, Debit(Receivables, 99m), Credit(Sales, 99m)),
            _acme.Sealed("2026-03"));

        var snapshot = _projections.Rows<SnapshotKey, SnapshotRow>(ProjectionTables.PeriodSnapshots);

        Assert.Equal(2, snapshot.Count);
        Assert.Equal(new SnapshotRow(50m, 50m, snapshot.Values.First().SealSequence, 2), snapshot[new SnapshotKey("ACME", "2026-03", Receivables, Currency)]);
    }

    [Fact]
    public async Task Une_periode_scellee_sans_ecriture_n_a_pas_de_ligne_d_instantane()
    {
        await _projections.DeliverAsync(_acme.Opened("2026-03"), _acme.Sealed("2026-03"));

        Assert.Empty(_projections.Rows<SnapshotKey, SnapshotRow>(ProjectionTables.PeriodSnapshots));
        Assert.Single(_projections.Rows<Guid, long>(ProjectionTables.Applied(ProjectionNames.PeriodSnapshots)));
    }
}
