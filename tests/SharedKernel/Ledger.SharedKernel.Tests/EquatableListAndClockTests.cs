namespace Ledger.SharedKernel.Tests;

public sealed class EquatableListAndClockTests
{
    [Fact]
    public void Listes_egales_element_par_element()
    {
        var a = EquatableList.Of(1, 2, 3);
        var b = EquatableList.Of(1, 2, 3);

        Assert.Equal(a, b);
        Assert.True(a.Equals((object)b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, EquatableList.Of(3, 2, 1));
        Assert.False(a.Equals(null));
    }

    [Fact]
    public void Liste_indexable_et_enumerable()
    {
        var list = EquatableList.Of("a", "b");

        Assert.Equal(2, list.Count);
        Assert.Equal("b", list[1]);
        Assert.Equal(["a", "b"], list);
        Assert.Equal(2, ((System.Collections.IEnumerable)list).Cast<string>().Count());
    }

    [Fact]
    public void Liste_independante_de_la_source()
    {
        var source = new List<int> { 1 };
        var list = new EquatableList<int>(source);
        source.Add(2);

        Assert.Single(list);
    }

    [Fact]
    public void Horloge_systeme_lit_le_TimeProvider()
    {
        var before = DateTimeOffset.UtcNow;
        var now = new SystemClock().UtcNow;

        Assert.InRange(now, before.AddSeconds(-1), DateTimeOffset.UtcNow.AddSeconds(1));
    }
}
