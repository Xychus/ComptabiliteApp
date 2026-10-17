using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Entries.Rules;
using Ledger.SharedKernel;
using static Ledger.Command.Domain.Tests.Support.TestLedger;

namespace Ledger.Command.Domain.Tests.Entries.Rules;

public sealed class AtLeastTwoLinesRuleTests
{
    private readonly AtLeastTwoLinesRule _rule = new();

    [Fact]
    public void Deux_lignes_acceptees() =>
        Assert.Null(_rule.Check(Lines(Debit(Cash, 1m), Credit(Sales, 1m)), Chart));

    [Fact]
    public void Une_seule_ligne_rejetee()
    {
        var error = _rule.Check(Lines(Debit(Cash, 1m)), Chart);

        Assert.Equal(EntryErrors.TooFewLinesCode, error?.Code);
        Assert.Equal("1", error!.Details!["lineCount"]);
    }
}

public sealed class StrictlyPositiveAmountsRuleTests
{
    private readonly StrictlyPositiveAmountsRule _rule = new();

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public void Montant_nul_ou_negatif_rejete_avec_la_ligne_fautive(string amount)
    {
        var error = _rule.Check(Lines(Debit(Cash, 5m), Credit(Sales, decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture))), Chart);

        Assert.Equal(EntryErrors.NonPositiveAmountCode, error?.Code);
        Assert.Equal("1", error!.Details!["lineIndex"]);
        Assert.Equal("4000", error.Details["account"]);
    }

    [Fact]
    public void Montants_positifs_acceptes() =>
        Assert.Null(_rule.Check(Lines(Debit(Cash, 0.01m), Credit(Sales, 0.01m)), Chart));
}

public sealed class MinorUnitPrecisionRuleTests
{
    private readonly MinorUnitPrecisionRule _rule = new();

    [Fact]
    public void Fraction_de_cent_rejetee()
    {
        var error = _rule.Check(Lines(Debit(Cash, 9.975m), Credit(Sales, 9.975m)), Chart);

        Assert.Equal(EntryErrors.InvalidPrecisionCode, error?.Code);
        Assert.Equal("0", error!.Details!["lineIndex"]);
    }

    [Fact]
    public void Montant_au_cent_accepte() =>
        Assert.Null(_rule.Check(Lines(Debit(Cash, 9.98m), Credit(Sales, 9.98m)), Chart));
}

public sealed class SingleCurrencyRuleTests
{
    private readonly SingleCurrencyRule _rule = new();

    [Fact]
    public void Devises_melangees_rejetees()
    {
        var error = _rule.Check(Lines(Debit(Cash, 1m), Credit(UsdBank, 1m)), Chart);

        Assert.Equal(EntryErrors.MixedCurrenciesCode, error?.Code);
        Assert.Equal("1", error!.Details!["lineIndex"]);
    }

    [Fact]
    public void Devise_unique_acceptee() =>
        Assert.Null(_rule.Check(Lines(Debit(Cash, 1m), Credit(Sales, 1m)), Chart));

    [Fact]
    public void Aucune_ligne_laissee_aux_autres_regles() =>
        Assert.Null(_rule.Check(Lines(), Chart));
}

public sealed class ActiveAccountRuleTests
{
    private readonly ActiveAccountRule _rule = new();

    [Fact]
    public void Compte_inactif_rejete()
    {
        var error = _rule.Check(Lines(Debit(Closed, 1m), Credit(Sales, 1m)), Chart);

        Assert.Equal(EntryErrors.InactiveAccountCode, error?.Code);
        Assert.Equal("9999", error!.Details!["account"]);
    }

    [Fact]
    public void Compte_inconnu_rejete()
    {
        var unknown = new JournalLine(new AccountId("0000"), Side.Debit, Cad(1m));

        Assert.Equal(EntryErrors.InactiveAccountCode, _rule.Check(Lines(unknown, Credit(Sales, 1m)), Chart)?.Code);
    }

    [Fact]
    public void Comptes_actifs_acceptes() =>
        Assert.Null(_rule.Check(Lines(Debit(Cash, 1m), Credit(Sales, 1m)), Chart));
}

public sealed class AccountCurrencyRuleTests
{
    private readonly AccountCurrencyRule _rule = new();

    [Fact]
    public void Devise_differente_de_celle_du_compte_rejetee()
    {
        var usdOnCadAccount = new JournalLine(Cash.Id, Side.Debit, new Money(1m, Currency.Usd));

        var error = _rule.Check(Lines(usdOnCadAccount, Credit(UsdBank, 1m)), Chart);

        Assert.Equal(EntryErrors.AccountCurrencyMismatchCode, error?.Code);
        Assert.Equal("0", error!.Details!["lineIndex"]);
    }

    [Fact]
    public void Devise_du_compte_acceptee() =>
        Assert.Null(_rule.Check(Lines(Debit(Cash, 1m), Credit(Sales, 1m)), Chart));
}

public sealed class BalancedPerCurrencyRuleTests
{
    private readonly BalancedPerCurrencyRule _rule = new();

    [Fact]
    public void Ecriture_desequilibree_rejetee_avec_les_totaux()
    {
        var error = _rule.Check(Lines(Debit(Receivables, 114.98m), Credit(Sales, 100m), Credit(GstPayable, 5m)), Chart);

        Assert.Equal(EntryErrors.NotBalancedCode, error?.Code);
        Assert.Equal("CAD", error!.Details!["currency"]);
        Assert.Equal("114.98", error.Details["debits"]);
        Assert.Equal("105", error.Details["credits"]);
    }

    [Fact]
    public void Ecriture_equilibree_acceptee() =>
        Assert.Null(_rule.Check(Lines(Debit(Receivables, 105m), Credit(Sales, 100m), Credit(GstPayable, 5m)), Chart));

    [Fact]
    public void Equilibre_verifie_devise_par_devise()
    {
        var lines = Lines(Debit(Cash, 1m), Credit(Sales, 1m), Debit(UsdBank, 2m), Credit(UsdBank, 1m));

        Assert.Equal("USD", _rule.Check(lines, Chart)?.Details!["currency"]);
    }
}

public sealed class EntryRuleSetTests
{
    [Fact]
    public void La_premiere_regle_violee_l_emporte()
    {
        // Une seule ligne, déséquilibrée : la règle structurelle passe avant l'équilibre.
        var error = EntryRuleSet.Default.Check(Lines(Debit(Cash, 1m)), Chart);

        Assert.Equal(EntryErrors.TooFewLinesCode, error?.Code);
    }

    [Fact]
    public void Ensemble_extensible_par_ajout_d_une_regle()
    {
        var rules = new EntryRuleSet([new AtLeastTwoLinesRule(), new RejectEverything()]);

        Assert.Equal("Custom", rules.Check(Lines(Debit(Cash, 1m), Credit(Sales, 1m)), Chart)?.Code);
    }

    [Fact]
    public void Ecriture_valide_passe_toutes_les_regles() =>
        Assert.Null(EntryRuleSet.Default.Check(Lines(Debit(Cash, 1m), Credit(Sales, 1m)), Chart));

    private sealed class RejectEverything : IEntryRule
    {
        public DomainError? Check(IReadOnlyList<JournalLine> lines, Accounts.IChartOfAccounts chart) => new("Custom", "Toujours rejeté");
    }
}
