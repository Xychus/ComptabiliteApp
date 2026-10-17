namespace Ledger.SharedKernel;

/// <summary>Entité comptable (société) qui tient son propre grand livre.</summary>
public readonly record struct EntityId
{
    public EntityId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>Code d'un compte du plan comptable (ex. <c>1100</c>).</summary>
public readonly record struct AccountId
{
    public AccountId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>
/// Identifiant d'écriture : UUIDv7 choisi par le client. Il sert d'identité et de clé
/// d'idempotence ; il ne sert jamais à ordonner les écritures (l'ordre vient du journal).
/// </summary>
public readonly record struct EntryId
{
    public EntryId(Guid value)
    {
        if (value.Version != 7)
        {
            throw new ArgumentException("Un identifiant d'écriture doit être un UUIDv7.", nameof(value));
        }

        Value = value;
    }

    public Guid Value { get; }

    public static EntryId New() => new(Guid.CreateVersion7());

    public static bool TryCreate(Guid value, out EntryId entryId)
    {
        entryId = value.Version == 7 ? new EntryId(value) : default;
        return value.Version == 7;
    }

    public override string ToString() => Value.ToString();
}
