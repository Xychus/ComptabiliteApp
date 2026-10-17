using Ledger.Command.Domain.Accounts;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Tests.Support;
using Ledger.SharedKernel;
using static Ledger.Command.Domain.Tests.Support.TestLedger;

namespace Ledger.Command.Domain.Tests.ChartOfAccounts;

/// <summary>Plan comptable tenu dans le journal (ADR 0012) : événements passés → définition → événements ou rejet.</summary>
public sealed class ChartDeciderTests
{
    private static readonly AccountDefined CashDefined = new(Acme, Cash.Id, "Encaisse", Currency.Cad, Side.Debit, IsActive: true, NoNegativeBalance: true);

    private static Scenario<ChartState> AcmeChart() => new(ChartState.Initial(Acme), (state, e) => state.Evolve(e));

    private static DefineAccount Define(string name = "Encaisse", Currency? currency = null, Side side = Side.Debit, bool isActive = true, bool noNegativeBalance = true) =>
        new(Acme, Cash.Id, name, currency ?? Currency.Cad, side, isActive, noNegativeBalance);

    [Fact]
    public void Definir_un_compte_inconnu() =>
        AcmeChart()
            .When(s => ChartDecider.Decide(s, Define()))
            .Then(CashDefined);

    [Fact]
    public void Une_definition_identique_ne_produit_aucun_evenement() =>
        AcmeChart()
            .Given(CashDefined)
            .When(s => ChartDecider.Decide(s, Define()))
            .Then();

    [Fact]
    public void Renommer_et_desactiver_un_compte() =>
        AcmeChart()
            .Given(CashDefined)
            .When(s => ChartDecider.Decide(s, Define(name: "Caisse", isActive: false)))
            .Then(CashDefined with { Name = "Caisse", IsActive = false });

    [Fact]
    public void La_devise_est_immuable()
    {
        var error = AcmeChart()
            .Given(CashDefined)
            .When(s => ChartDecider.Decide(s, Define(currency: Currency.Usd)))
            .ThenRejected(AccountErrors.AttributeImmutableCode);

        Assert.Equal("currency", error.Details!["attribute"]);
        Assert.Equal("1000", error.Details["account"]);
    }

    [Fact]
    public void Le_sens_normal_est_immuable() =>
        Assert.Equal(
            "normalSide",
            AcmeChart().Given(CashDefined).When(s => ChartDecider.Decide(s, Define(side: Side.Credit))).ThenRejected(AccountErrors.AttributeImmutableCode).Details!["attribute"]);

    [Fact]
    public void La_contrainte_de_solde_est_immuable() =>
        Assert.Equal(
            "noNegativeBalance",
            AcmeChart().Given(CashDefined).When(s => ChartDecider.Decide(s, Define(noNegativeBalance: false))).ThenRejected(AccountErrors.AttributeImmutableCode).Details!["attribute"]);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Un_nom_vide_est_rejete(string name) =>
        AcmeChart()
            .When(s => ChartDecider.Decide(s, Define(name: name)))
            .ThenRejected(AccountErrors.InvalidNameCode);

    [Fact]
    public void Un_nom_trop_long_est_rejete() =>
        AcmeChart()
            .When(s => ChartDecider.Decide(s, Define(name: new string('x', ChartDecider.MaxNameLength + 1))))
            .ThenRejected(AccountErrors.InvalidNameCode);

    [Fact]
    public void Un_nom_a_la_limite_est_accepte() =>
        AcmeChart()
            .When(s => ChartDecider.Decide(s, Define(name: new string('x', ChartDecider.MaxNameLength))))
            .Then(CashDefined with { Name = new string('x', ChartDecider.MaxNameLength) });

    [Fact]
    public void Commande_pour_une_autre_entite_que_l_etat_est_un_bogue() =>
        Assert.Throws<ArgumentException>(() => ChartDecider.Decide(ChartState.Initial(new EntityId("AUTRE")), Define()));
}

public sealed class ChartStateTests
{
    [Fact]
    public void Le_plan_comptable_restitue_l_etat_courant_de_chaque_compte()
    {
        var state = ChartState.Initial(Acme)
            .Evolve(new AccountDefined(Acme, Cash.Id, "Encaisse", Currency.Cad, Side.Debit, IsActive: true, NoNegativeBalance: true))
            .Evolve(new AccountDefined(Acme, Sales.Id, "Ventes", Currency.Cad, Side.Credit, IsActive: true, NoNegativeBalance: false))
            .Evolve(new AccountDefined(Acme, Cash.Id, "Caisse", Currency.Cad, Side.Debit, IsActive: false, NoNegativeBalance: true));

        Assert.Equal(Cash with { IsActive = false }, state.Find(Cash.Id));
        Assert.Equal(Sales, state.Find(Sales.Id));
        Assert.Null(state.Find(Receivables.Id));
    }

    [Fact]
    public void Un_compte_d_une_autre_entite_ou_un_evenement_etranger_est_ignore()
    {
        var state = ChartState.Initial(Acme);

        var evolved = state
            .Evolve(new AccountDefined(new EntityId("AUTRE"), Cash.Id, "Encaisse", Currency.Cad, Side.Debit, IsActive: true, NoNegativeBalance: true))
            .Evolve(new UnrelatedEvent());

        Assert.Null(evolved.Find(Cash.Id));
    }

    private sealed record UnrelatedEvent : IDomainEvent;
}
