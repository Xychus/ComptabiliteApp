using Ledger.Command.Application.Accounts;
using Ledger.Command.Application.EventStore;
using Ledger.Command.Application.Tests.Support;
using Ledger.Command.Domain.Accounts;
using Ledger.Command.Domain.Entries;
using Ledger.Contracts.Events.V1;
using Ledger.SharedKernel;
using static Ledger.Command.Application.Tests.Support.InMemoryLedger;
using InMemoryLedger = Ledger.Command.Application.Tests.Support.InMemoryLedger;

namespace Ledger.Command.Application.Tests.Accounts;

/// <summary>Plan comptable tenu dans le flux <c>chart-{entité}</c> (ADR 0012).</summary>
public sealed class DefineAccountHandlerTests
{
    private static readonly EntityId _globex = new("GLOBEX");
    private readonly InMemoryLedger _ledger = new();

    private static DefineAccount Bank(string name = "Banque", bool isActive = true) =>
        new(_globex, new AccountId("1020"), name, Currency.Cad, Side.Debit, isActive, NoNegativeBalance: true);

    [Fact]
    public async Task Definir_un_compte_ecrit_AccountDefined_dans_la_periode_de_l_horloge_serveur()
    {
        var clock = new FixedClock(new DateTimeOffset(2026, 10, 31, 23, 59, 59, TimeSpan.Zero));

        var result = await _ledger.DefineHandler(_ledger.Store.BeginUnitOfWork(), clock).HandleAsync(Bank(), Metadata, Ct);

        var stored = Assert.Single(await _ledger.ReadAsync(StreamIds.Chart(_globex)));
        Assert.Equal(EventTypes.AccountDefined, stored.EventType);
        Assert.Equal("2026-10", stored.Period);
        Assert.Null(stored.BusinessDate);
        Assert.Equal(new AccountReceipt(_globex, new AccountId("1020"), 1, stored.GlobalSequence, stored.RecordedAt, stored.Hash, Created: true), result.Value);
    }

    [Fact]
    public async Task Une_definition_identique_renvoie_l_evenement_courant_sans_rien_ecrire()
    {
        var first = await _ledger.DefineAsync(Bank());
        await _ledger.DefineAsync(Bank() with { Account = new AccountId("4000"), NormalSide = Side.Credit, NoNegativeBalance = false });

        var replay = await _ledger.DefineAsync(Bank());

        Assert.Equal(first.Value with { ChartVersion = 2, Created = false }, replay.Value);
        Assert.Equal(2, (await _ledger.ReadAsync(StreamIds.Chart(_globex))).Count);
    }

    [Fact]
    public async Task Modifier_un_compte_ajoute_un_evenement_et_le_plan_relu_en_tient_compte()
    {
        await _ledger.DefineAsync(Bank());

        var result = await _ledger.DefineAsync(Bank("Banque Nationale", isActive: false));

        Assert.Equal((2L, false), (result.Value.ChartVersion, result.Value.Created));
        var chart = await _ledger.Chart.LoadAsync(_globex, Ct);
        Assert.Equal(new AccountInfo(new AccountId("1020"), Currency.Cad, Side.Debit, IsActive: false, NoNegativeBalance: true), chart.Find(new AccountId("1020")));
    }

    [Fact]
    public async Task Un_attribut_immuable_modifie_est_rejete_sans_rien_ecrire()
    {
        await _ledger.DefineAsync(Bank());

        var result = await _ledger.DefineAsync(Bank() with { Currency = Currency.Usd });

        Assert.Equal(AccountErrors.AttributeImmutableCode, result.Error.Code);
        Assert.Single(await _ledger.ReadAsync(StreamIds.Chart(_globex)));
    }

    [Fact]
    public async Task Un_compte_defini_en_concurrence_relance_lecture_et_decision()
    {
        var racing = new RacingUnitOfWork(
            _ledger.Store.BeginUnitOfWork(),
            async () => Assert.True((await _ledger.DefineAsync(Bank() with { Account = new AccountId("4000"), NormalSide = Side.Credit })).IsSuccess));

        var result = await _ledger.DefineHandler(racing).HandleAsync(Bank(), Metadata, Ct);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(2, racing.Commits);
        Assert.Equal(2, result.Value.ChartVersion);
    }

    [Fact]
    public async Task Le_plan_d_une_entite_ne_voit_pas_les_comptes_d_une_autre()
    {
        await _ledger.DefineAsync(Bank());

        Assert.Null((await _ledger.Chart.LoadAsync(Acme, Ct)).Find(new AccountId("1020")));
        Assert.NotNull((await _ledger.Chart.LoadAsync(_globex, Ct)).Find(new AccountId("1020")));
    }

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
