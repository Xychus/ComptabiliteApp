using Ledger.Command.Application.EventStore;
using Ledger.Command.Domain.Periods;
using Ledger.SharedKernel;

namespace Ledger.Command.Application.Tests.EventStore;

public sealed class StreamIdsTests
{
    [Fact]
    public void Noms_des_flux()
    {
        var entryId = new EntryId(Guid.Parse("01920000-0000-7000-8000-000000000001"));

        Assert.Equal("entry-01920000-0000-7000-8000-000000000001", StreamIds.Entry(entryId));
        Assert.Equal("account-ACME-1000", StreamIds.Account(new EntityId("ACME"), new AccountId("1000")));
        Assert.Equal("period-ACME-2026-03", StreamIds.Period(new EntityId("ACME"), new PeriodId(2026, 3)));
    }

    [Theory]
    [InlineData("A-B", "C", "A", "B-C")]
    [InlineData("A%2DB", "C", "A-B", "C")]
    public void Deux_couples_distincts_ne_donnent_jamais_le_meme_flux(string entity1, string account1, string entity2, string account2) =>
        Assert.NotEqual(
            StreamIds.Account(new EntityId(entity1), new AccountId(account1)),
            StreamIds.Account(new EntityId(entity2), new AccountId(account2)));

    [Fact]
    public void Les_composants_sont_echappes() =>
        Assert.Equal("account-ACME%2DQC-10%2500", StreamIds.Account(new EntityId("ACME-QC"), new AccountId("10%00")));
}
