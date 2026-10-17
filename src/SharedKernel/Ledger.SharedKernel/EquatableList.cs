using System.Collections;

namespace Ledger.SharedKernel;

/// <summary>
/// Liste immuable à égalité de valeur (élément par élément), pour que les événements et commandes
/// définis en <c>record</c> restent comparables quand ils contiennent des lignes.
/// </summary>
public sealed class EquatableList<T> : IReadOnlyList<T>, IEquatable<EquatableList<T>>
{
    private readonly T[] _items;

    public EquatableList(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        _items = [.. items];
    }

    public int Count => _items.Length;

    public T this[int index] => _items[index];

    public bool Equals(EquatableList<T>? other) =>
        other is not null && _items.AsSpan().SequenceEqual(other._items, EqualityComparer<T>.Default);

    public override bool Equals(object? obj) => Equals(obj as EquatableList<T>);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var item in _items)
        {
            hash.Add(item);
        }

        return hash.ToHashCode();
    }

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_items).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Fabriques de <see cref="EquatableList{T}"/>.</summary>
public static class EquatableList
{
    public static EquatableList<T> Of<T>(params IEnumerable<T> items) => new(items);
}
