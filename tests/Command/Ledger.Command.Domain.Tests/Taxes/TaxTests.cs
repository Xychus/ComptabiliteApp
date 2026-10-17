using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Taxes;
using Ledger.SharedKernel;
using static Ledger.Command.Domain.Tests.Support.TestLedger;

namespace Ledger.Command.Domain.Tests.Taxes;

public sealed class QuebecSalesTaxCalculatorTests
{
    [Fact]
    public void Exemple_de_reference_vente_de_100_dollars()
    {
        var taxes = new QuebecSalesTaxCalculator().Calculate([Cad(100m)]);

        Assert.Equal(Cad(100m), taxes.Subtotal);
        Assert.Equal(Cad(5.00m), taxes.Gst);
        Assert.Equal(Cad(9.98m), taxes.Qst);
        Assert.Equal(Cad(114.98m), taxes.Total);
    }

    [Fact]
    public void Arrondi_au_niveau_de_la_facture_par_defaut()
    {
        var taxes = new QuebecSalesTaxCalculator().Calculate(TenLinesOfFiveCents());

        Assert.Equal(Cad(0.50m), taxes.Subtotal);
        Assert.Equal(Cad(0.03m), taxes.Gst);
        Assert.Equal(Cad(0.05m), taxes.Qst);
    }

    [Fact]
    public void Arrondi_au_niveau_de_la_ligne_sur_option()
    {
        var calculator = new QuebecSalesTaxCalculator(new TaxRoundingPolicy(TaxRoundingLevel.Line, MidpointRounding.AwayFromZero));

        var taxes = calculator.Calculate(TenLinesOfFiveCents());

        Assert.Equal(Cad(0.50m), taxes.Subtotal);
        Assert.Equal(Cad(0.00m), taxes.Gst);
        Assert.Equal(Cad(0.00m), taxes.Qst);
    }

    [Fact]
    public void Mode_d_arrondi_du_demi_cent_parametrable()
    {
        // TPS sur 0,50 $ = 0,025 : AwayFromZero → 0,03 ; ToEven → 0,02.
        var toEven = new QuebecSalesTaxCalculator(TaxRoundingPolicy.Default with { Mode = MidpointRounding.ToEven });

        Assert.Equal(Cad(0.02m), toEven.Calculate([Cad(0.50m)]).Gst);
    }

    [Fact]
    public void Facture_vide_est_un_bogue() =>
        Assert.Throws<ArgumentException>(() => new QuebecSalesTaxCalculator().Calculate([]));

    [Fact]
    public void Politique_par_defaut_documentee_dans_l_ADR_0008()
    {
        Assert.Equal(TaxRoundingLevel.Invoice, TaxRoundingPolicy.Default.Level);
        Assert.Equal(MidpointRounding.AwayFromZero, TaxRoundingPolicy.Default.Mode);
    }

    private static Money[] TenLinesOfFiveCents() => [.. Enumerable.Repeat(Cad(0.05m), 10)];
}

public sealed class ReferenceSaleExampleTests
{
    [Fact]
    public void Vente_de_100_dollars_taxee_produit_une_ecriture_equilibree_et_acceptee()
    {
        // Exemple de référence du plan : clients 114,98 / ventes 100,00 / TPS 5,00 / TVQ 9,98.
        var taxes = new QuebecSalesTaxCalculator().Calculate([Cad(100m)]);
        var lines = Lines(
            new JournalLine(Receivables.Id, Side.Debit, taxes.Total),
            new JournalLine(Sales.Id, Side.Credit, taxes.Subtotal),
            new JournalLine(GstPayable.Id, Side.Credit, taxes.Gst),
            new JournalLine(QstPayable.Id, Side.Credit, taxes.Qst));
        var command = new PostEntry(EntryId.New(), Acme, March15, "Vente taxable", lines);

        var result = JournalEntryDecider.Default.Decide(JournalEntryState.Initial(command.EntryId), command, OpenMarch, Chart);

        Assert.True(result.IsSuccess);
        var posted = Assert.IsType<JournalEntryPosted>(Assert.Single(result.Value));
        Assert.Equal([114.98m, 100.00m, 5.00m, 9.98m], posted.Lines.Select(l => l.Amount.Amount));
    }
}
