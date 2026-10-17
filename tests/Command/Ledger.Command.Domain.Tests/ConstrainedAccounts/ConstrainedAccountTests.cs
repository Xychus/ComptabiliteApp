using Ledger.Command.Domain.ConstrainedAccounts;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Periods;
using Ledger.Command.Domain.Tests.Support;
using Ledger.SharedKernel;
using static Ledger.Command.Domain.Tests.Support.TestLedger;

namespace Ledger.Command.Domain.Tests.ConstrainedAccounts;

public sealed class ConstrainedAccountTests
{
    private static readonly EntryId _source = EntryId.New();

    private static Scenario<ConstrainedAccountState> Account(Accounts.AccountInfo account) =>
        new(ConstrainedAccountState.Initial(Acme, account), (state, e) => state.Evolve(e));

    private static AccountLinePosted Posted(Accounts.AccountInfo account, Side side, decimal amount, decimal balanceAfter) =>
        new(Acme, account.Id, _source, side, Cad(amount), Cad(balanceAfter));

    [Fact]
    public void Depot_sur_un_actif_augmente_le_solde() =>
        Account(Cash)
            .When(s => ConstrainedAccountDecider.Decide(s, new ApplyAccountLine(_source, Debit(Cash, 100m))))
            .Then(Posted(Cash, Side.Debit, 100m, 100m));

    [Fact]
    public void Retrait_couvert_par_le_solde_accepte() =>
        Account(Cash)
            .Given(Posted(Cash, Side.Debit, 100m, 100m))
            .When(s => ConstrainedAccountDecider.Decide(s, new ApplyAccountLine(_source, Credit(Cash, 60m))))
            .Then(Posted(Cash, Side.Credit, 60m, 40m));

    [Fact]
    public void Retrait_qui_vide_exactement_le_compte_accepte() =>
        Account(Cash)
            .Given(Posted(Cash, Side.Debit, 100m, 100m))
            .When(s => ConstrainedAccountDecider.Decide(s, new ApplyAccountLine(_source, Credit(Cash, 100m))))
            .Then(Posted(Cash, Side.Credit, 100m, 0m));

    [Fact]
    public void Retrait_superieur_au_solde_rejete()
    {
        var error = Account(Cash)
            .Given(Posted(Cash, Side.Debit, 100m, 100m), Posted(Cash, Side.Credit, 60m, 40m))
            .When(s => ConstrainedAccountDecider.Decide(s, new ApplyAccountLine(_source, Credit(Cash, 60m))))
            .ThenRejected(ConstrainedAccountDecider.InsufficientBalanceCode);

        Assert.Equal("40", error.Details!["balance"]);
        Assert.Equal("-60", error.Details["delta"]);
        Assert.Equal("2", error.Details["version"]);
    }

    [Fact]
    public void Passif_contraint_mesure_dans_le_sens_credit()
    {
        // Dépôts clients (passif) : un crédit augmente le solde, un débit le diminue (ADR 0008, H7).
        Account(CustomerDeposits)
            .Given(Posted(CustomerDeposits, Side.Credit, 50m, 50m))
            .When(s => ConstrainedAccountDecider.Decide(s, new ApplyAccountLine(_source, Debit(CustomerDeposits, 30m))))
            .Then(Posted(CustomerDeposits, Side.Debit, 30m, 20m));

        Account(CustomerDeposits)
            .When(s => ConstrainedAccountDecider.Decide(s, new ApplyAccountLine(_source, Debit(CustomerDeposits, 0.01m))))
            .ThenRejected(ConstrainedAccountDecider.InsufficientBalanceCode);
    }

    [Fact]
    public void La_version_suit_le_nombre_de_lignes_appliquees()
    {
        var state = new[] { Posted(Cash, Side.Debit, 10m, 10m), Posted(Cash, Side.Debit, 5m, 15m) }
            .Aggregate(ConstrainedAccountState.Initial(Acme, Cash), (s, e) => s.Evolve(e));

        Assert.Equal(2, state.Version);
        Assert.Equal(Cad(15m), state.Balance);
        Assert.Equal(state, state.Evolve(new PeriodOpened(Acme, March)));
    }

    [Fact]
    public void Compte_non_contraint_est_un_bogue() =>
        Assert.Throws<ArgumentException>(() =>
            ConstrainedAccountDecider.Decide(ConstrainedAccountState.Initial(Acme, Receivables), new ApplyAccountLine(_source, Debit(Receivables, 1m))));

    [Fact]
    public void Ligne_d_un_autre_compte_est_un_bogue() =>
        Assert.Throws<ArgumentException>(() =>
            ConstrainedAccountDecider.Decide(ConstrainedAccountState.Initial(Acme, Cash), new ApplyAccountLine(_source, Debit(Receivables, 1m))));
}
