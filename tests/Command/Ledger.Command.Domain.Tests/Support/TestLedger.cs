using Ledger.Command.Domain.Accounts;
using Ledger.Command.Domain.Entries;
using Ledger.Command.Domain.Periods;
using Ledger.SharedKernel;

namespace Ledger.Command.Domain.Tests.Support;

/// <summary>Données de test partagées : une entité, un plan comptable, des périodes.</summary>
internal static class TestLedger
{
    public static readonly EntityId Acme = new("ACME");

    public static readonly AccountInfo Cash = new(new AccountId("1000"), Currency.Cad, Side.Debit, IsActive: true, NoNegativeBalance: true);
    public static readonly AccountInfo Receivables = new(new AccountId("1100"), Currency.Cad, Side.Debit, IsActive: true, NoNegativeBalance: false);
    public static readonly AccountInfo UsdBank = new(new AccountId("1050"), Currency.Usd, Side.Debit, IsActive: true, NoNegativeBalance: false);
    public static readonly AccountInfo GstPayable = new(new AccountId("2300"), Currency.Cad, Side.Credit, IsActive: true, NoNegativeBalance: false);
    public static readonly AccountInfo QstPayable = new(new AccountId("2310"), Currency.Cad, Side.Credit, IsActive: true, NoNegativeBalance: false);
    public static readonly AccountInfo CustomerDeposits = new(new AccountId("2400"), Currency.Cad, Side.Credit, IsActive: true, NoNegativeBalance: true);
    public static readonly AccountInfo Sales = new(new AccountId("4000"), Currency.Cad, Side.Credit, IsActive: true, NoNegativeBalance: false);
    public static readonly AccountInfo Closed = new(new AccountId("9999"), Currency.Cad, Side.Debit, IsActive: false, NoNegativeBalance: false);

    public static readonly IChartOfAccounts Chart = new InMemoryChart(Cash, Receivables, UsdBank, GstPayable, QstPayable, CustomerDeposits, Sales, Closed);

    public static readonly DateOnly March15 = new(2026, 3, 15);
    public static readonly PeriodId March = new(2026, 3);
    public static readonly PeriodId April = new(2026, 4);

    public static Money Cad(decimal amount) => new(amount, Currency.Cad);

    public static JournalLine Debit(AccountInfo account, decimal amount) => new(account.Id, Side.Debit, new Money(amount, account.Currency));

    public static JournalLine Credit(AccountInfo account, decimal amount) => new(account.Id, Side.Credit, new Money(amount, account.Currency));

    public static EquatableList<JournalLine> Lines(params JournalLine[] lines) => EquatableList.Of(lines);

    public static PeriodState Period(PeriodId period, PeriodStatus status) => new(Acme, period, status);

    public static PeriodState OpenMarch => Period(March, PeriodStatus.Open);

    private sealed class InMemoryChart(params AccountInfo[] accounts) : IChartOfAccounts
    {
        private readonly Dictionary<AccountId, AccountInfo> _accounts = accounts.ToDictionary(a => a.Id);

        public AccountInfo? Find(AccountId account) => _accounts.GetValueOrDefault(account);
    }
}
