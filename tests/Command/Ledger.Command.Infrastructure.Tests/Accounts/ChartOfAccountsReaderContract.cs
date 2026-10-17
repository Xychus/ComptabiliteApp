using Ledger.Command.Application.Accounts;
using Ledger.Command.Domain.Accounts;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Infrastructure.InMemory;
using Ledger.Command.Infrastructure.Postgres;
using Ledger.Command.Infrastructure.Tests.Postgres;
using Ledger.SharedKernel;

namespace Ledger.Command.Infrastructure.Tests.Accounts;

/// <summary>Suite de contrat de <see cref="IChartOfAccountsReader"/>.</summary>
public abstract class ChartOfAccountsReaderContract
{
    private static readonly AccountInfo _cash = new(new AccountId("1000"), Currency.Cad, Side.Debit, IsActive: true, NoNegativeBalance: true);
    private static readonly AccountInfo _usdSales = new(new AccountId("4100"), Currency.Usd, Side.Credit, IsActive: false, NoNegativeBalance: false);
    private readonly EntityId _entity = new("CHART-" + Guid.CreateVersion7().ToString("N"));

    protected abstract IChartOfAccountsReader Reader { get; }

    protected abstract Task DefineAsync(EntityId entity, AccountInfo account);

    [Fact]
    public async Task Les_comptes_definis_sont_restitues_avec_tous_leurs_attributs()
    {
        await DefineAsync(_entity, _cash);
        await DefineAsync(_entity, _usdSales);

        var chart = await Reader.LoadAsync(_entity, TestContext.Current.CancellationToken);

        Assert.Equal(_cash, chart.Find(_cash.Id));
        Assert.Equal(_usdSales, chart.Find(_usdSales.Id));
    }

    [Fact]
    public async Task Un_compte_inconnu_ou_d_une_autre_entite_est_absent()
    {
        await DefineAsync(new EntityId(_entity.Value + "-B"), _cash);

        var chart = await Reader.LoadAsync(_entity, TestContext.Current.CancellationToken);

        Assert.Null(chart.Find(_cash.Id));
    }
}

public sealed class InMemoryChartOfAccountsTests : ChartOfAccountsReaderContract
{
    private readonly InMemoryChartOfAccounts _chart = new();

    protected override IChartOfAccountsReader Reader => _chart;

    protected override Task DefineAsync(EntityId entity, AccountInfo account)
    {
        _chart.Define(entity, account);
        return Task.CompletedTask;
    }
}

[Collection(PostgresGroup.Name)]
[Trait("Category", "Integration")]
public sealed class NpgsqlChartOfAccountsReaderTests(PostgresFixture database) : ChartOfAccountsReaderContract
{
    protected override IChartOfAccountsReader Reader { get; } = new NpgsqlChartOfAccountsReader(database.AppDataSource);

    protected override Task DefineAsync(EntityId entity, AccountInfo account) => database.DefineAccountAsync(entity, account);
}
