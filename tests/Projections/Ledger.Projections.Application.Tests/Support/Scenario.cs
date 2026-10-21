using Ledger.Contracts.Events.V1;
using static Ledger.Projections.Tests.Support.JournalBuilder;

namespace Ledger.Projections.Tests.Support;

/// <summary>
/// Jeu d'essai qui touche toutes les projections : plan comptable, deux entités, ventes et achats
/// taxables, contre-passation dans la période suivante, ligne de compte contraint, sceau.
/// </summary>
public static class Scenario
{
    public static int Count => Build().Length;

    public static EventEnvelopeV1[] Build(string entityId = "ACME", string otherEntityId = "AUTRE")
    {
        var acme = new JournalBuilder(entityId);
        var other = new JournalBuilder(otherEntityId) { NextSequence = 1000 };
        var sale = Guid.CreateVersion7();
        var refund = Guid.CreateVersion7();
        var cashLine = Debit("1000", 57.49m);
        return
        [
            acme.AccountDefined("1000", "Encaisse", noNegativeBalance: true),
            acme.AccountDefined("1100", "Clients"),
            acme.AccountDefined("2310", "TPS à percevoir", normalSide: "Credit"),
            acme.AccountDefined("2320", "TVQ à percevoir", normalSide: "Credit"),
            acme.AccountDefined("4000", "Ventes", normalSide: "Credit"),
            acme.Opened("2026-03"),
            acme.Posted(sale, new DateOnly(2026, 3, 2), Debit("1100", 114.98m), Credit("4000", 100m), Credit("2310", 5m), Credit("2320", 9.98m)),
            acme.Posted(refund, new DateOnly(2026, 3, 5), Debit("4000", 20m), Debit("2310", 1m), Debit("2320", 2m), Credit("1100", 23m)),
            acme.Posted(new DateOnly(2026, 3, 9), Debit("5100", 50m), Debit("1350", 2.5m), Debit("1360", 4.99m), cashLine with { Side = "Credit" }),
            acme.Posted(new DateOnly(2026, 3, 10), cashLine, Credit("1100", 57.49m)),
            acme.AccountLine(refund, new DateOnly(2026, 3, 10), cashLine, 57.49m),
            acme.Sealed("2026-03"),
            acme.Opened("2026-04"),
            acme.Reversed(sale, new DateOnly(2026, 4, 1)),
            acme.AccountDefined("1100", "Clients — Québec", period: "2026-04"),
            other.Opened("2026-03"),
            other.Posted(new DateOnly(2026, 3, 15), Debit("1000", 1000m, "USD"), Credit("3000", 1000m, "USD")),
            other.Sealed("2026-03"),
        ];
    }
}
