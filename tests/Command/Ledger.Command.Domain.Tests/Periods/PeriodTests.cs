using Ledger.Command.Domain.Periods;
using Ledger.Command.Domain.Tests.Support;
using static Ledger.Command.Domain.Tests.Support.TestLedger;

namespace Ledger.Command.Domain.Tests.Periods;

public sealed class PeriodIdTests
{
    [Theory]
    [InlineData(2026, 3, 1, "2026-03")]
    [InlineData(2026, 3, 31, "2026-03")]
    [InlineData(2026, 12, 31, "2026-12")]
    public void Periode_derivee_de_la_date_d_affaires(int year, int month, int day, string expected) =>
        Assert.Equal(expected, PeriodId.FromBusinessDate(new DateOnly(year, month, day)).ToString());

    [Fact]
    public void Analyse_d_une_periode_valide()
    {
        var result = PeriodId.Parse("2026-03");

        Assert.True(result.IsSuccess);
        Assert.Equal(March, result.Value);
    }

    [Theory]
    [InlineData("2026-13")]
    [InlineData("2026-3")]
    [InlineData("mars")]
    [InlineData("")]
    public void Analyse_d_une_periode_invalide(string value) =>
        Assert.Equal("InvalidPeriod", PeriodId.Parse(value).Error.Code);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(10000, 1)]
    [InlineData(2026, 0)]
    [InlineData(2026, 13)]
    public void Construction_hors_bornes_refusee(int year, int month) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeriodId(year, month));
}

public sealed class PeriodDeciderTests
{
    private static readonly PeriodDigest Digest = new(3, new string('a', 64));

    private static Scenario<PeriodState> MarchPeriod() =>
        new(PeriodState.Initial(Acme, March), (state, e) => state.Evolve(e));

    [Fact]
    public void Ouvrir_une_periode_jamais_ouverte() =>
        MarchPeriod()
            .When(s => PeriodDecider.Decide(s, new OpenPeriod(Acme, March)))
            .Then(new PeriodOpened(Acme, March));

    [Fact]
    public void Ouvrir_une_periode_deja_ouverte_est_rejete() =>
        MarchPeriod()
            .Given(new PeriodOpened(Acme, March))
            .When(s => PeriodDecider.Decide(s, new OpenPeriod(Acme, March)))
            .ThenRejected(PeriodErrors.AlreadyOpenCode);

    [Fact]
    public void Rouvrir_une_periode_scellee_est_rejete() =>
        MarchPeriod()
            .Given(new PeriodOpened(Acme, March), new PeriodSealed(Acme, March, Digest))
            .When(s => PeriodDecider.Decide(s, new OpenPeriod(Acme, March)))
            .ThenRejected(PeriodErrors.SealedCode);

    [Fact]
    public void Sceller_une_periode_ouverte() =>
        MarchPeriod()
            .Given(new PeriodOpened(Acme, March))
            .When(s => PeriodDecider.Decide(s, new SealPeriod(Acme, March, Digest)))
            .Then(new PeriodSealed(Acme, March, Digest));

    [Fact]
    public void Sceller_une_periode_jamais_ouverte_est_rejete() =>
        MarchPeriod()
            .When(s => PeriodDecider.Decide(s, new SealPeriod(Acme, March, Digest)))
            .ThenRejected(PeriodErrors.NotOpenCode);

    [Fact]
    public void Sceller_deux_fois_est_rejete() =>
        MarchPeriod()
            .Given(new PeriodOpened(Acme, March), new PeriodSealed(Acme, March, Digest))
            .When(s => PeriodDecider.Decide(s, new SealPeriod(Acme, March, Digest)))
            .ThenRejected(PeriodErrors.AlreadySealedCode);

    [Fact]
    public void Commande_pour_une_autre_periode_que_l_etat_est_un_bogue() =>
        Assert.Throws<ArgumentException>(() => PeriodDecider.Decide(PeriodState.Initial(Acme, March), new OpenPeriod(Acme, April)));

    [Fact]
    public void Un_evenement_etranger_ne_change_pas_l_etat()
    {
        var state = PeriodState.Initial(Acme, March);

        Assert.Equal(state, state.Evolve(new UnrelatedEvent()));
    }

    [Theory]
    [InlineData(PeriodStatus.Open, null)]
    [InlineData(PeriodStatus.NotOpened, PeriodErrors.NotOpenCode)]
    [InlineData(PeriodStatus.Sealed, PeriodErrors.SealedCode)]
    public void Une_ecriture_exige_une_periode_ouverte(PeriodStatus status, string? expectedCode)
    {
        var error = Period(March, status).RejectionForPosting();

        Assert.Equal(expectedCode, error?.Code);
        if (error is not null)
        {
            Assert.Equal("2026-03", error.Details!["period"]);
            Assert.Equal("ACME", error.Details["entity"]);
        }
    }

    private sealed record UnrelatedEvent : IDomainEvent;
}
