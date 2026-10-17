namespace Ledger.SharedKernel.Tests;

public sealed class IdentifierTests
{
    [Fact]
    public void EntryId_New_produit_un_UUIDv7()
    {
        var id = EntryId.New();

        Assert.Equal(7, id.Value.Version);
        Assert.Equal(id.Value.ToString(), id.ToString());
    }

    [Fact]
    public void EntryId_refuse_un_UUID_qui_n_est_pas_v7()
    {
        var v4 = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => new EntryId(v4));
        Assert.False(EntryId.TryCreate(v4, out _));
    }

    [Fact]
    public void EntryId_TryCreate_accepte_un_UUIDv7()
    {
        var v7 = Guid.CreateVersion7();

        Assert.True(EntryId.TryCreate(v7, out var id));
        Assert.Equal(v7, id.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Codes_vides_refuses(string value)
    {
        Assert.Throws<ArgumentException>(() => new AccountId(value));
        Assert.Throws<ArgumentException>(() => new EntityId(value));
    }

    [Fact]
    public void Codes_comparables_par_valeur()
    {
        Assert.Equal(new AccountId("1100"), new AccountId("1100"));
        Assert.Equal("1100", new AccountId("1100").ToString());
        Assert.Equal("ACME", new EntityId("ACME").ToString());
    }
}
