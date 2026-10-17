using System.Globalization;
using Ledger.Command.Application.EventStore;
using Ledger.Contracts.Integrity;
using Ledger.SharedKernel;

namespace Ledger.Command.Infrastructure.EventStore;

/// <summary>Dernière version d'un flux et son hash (<c>null</c> si le flux est vide).</summary>
public readonly record struct StreamHead(long Version, string? Hash);

/// <summary>
/// Règles communes à toutes les implémentations de l'event store pour préparer un lot :
/// versions consécutives à partir de la tête de chaque flux, horodatage serveur tronqué à la
/// microseconde, chaînage et hash (ADR 0005 et 0006). La séquence globale reste à attribuer.
/// </summary>
public static class AppendBatch
{
    /// <summary>Plus grande valeur absolue de <c>NUMERIC(20,2)</c>.</summary>
    private const decimal MaxStoredAmount = 999_999_999_999_999_999.99m;

    public static Result<IReadOnlyList<StoredEvent>> Prepare(
        IReadOnlyList<EventToAppend> events,
        IReadOnlyDictionary<string, StreamHead> heads,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(heads);
        var recordedAt = EventHash.TruncateToMicroseconds(now);
        var tips = new Dictionary<string, StreamHead>(heads, StringComparer.Ordinal);
        var eventIds = new HashSet<Guid>();
        var claims = new HashSet<Guid>();
        var prepared = new List<StoredEvent>(events.Count);
        foreach (var candidate in events)
        {
            EnsureStorable(candidate);
            var head = tips.GetValueOrDefault(candidate.StreamId);
            if (candidate.StreamVersion != head.Version + 1 || !eventIds.Add(candidate.EventId))
            {
                return Result.Failure<IReadOnlyList<StoredEvent>>(EventStoreErrors.ConcurrencyConflict(candidate.StreamId, candidate.StreamVersion));
            }

            if (candidate.ClaimedEntryId is { } claimed && !claims.Add(claimed))
            {
                return Result.Failure<IReadOnlyList<StoredEvent>>(EventStoreErrors.EntryIdAlreadyUsed(claimed));
            }

            var stored = Seal(candidate, recordedAt, head.Hash);
            tips[candidate.StreamId] = new StreamHead(stored.StreamVersion, stored.Hash);
            prepared.Add(stored);
        }

        return Result.Success<IReadOnlyList<StoredEvent>>(prepared);
    }

    private static StoredEvent Seal(EventToAppend candidate, DateTimeOffset recordedAt, string? previousHash)
    {
        var stored = new StoredEvent(
            GlobalSequence: 0,
            candidate.EventId,
            candidate.StreamId,
            candidate.StreamVersion,
            candidate.EventType,
            candidate.SchemaVersion,
            candidate.EntityId,
            candidate.BusinessDate,
            recordedAt,
            candidate.Period,
            candidate.Payload,
            candidate.BalanceAfter,
            candidate.Metadata,
            Hash: string.Empty,
            previousHash);
        return stored with { Hash = EventHash.Compute(stored.ToHashInput()) };
    }

    /// <summary>
    /// Mêmes contraintes que les <c>CHECK</c> de <c>ledger.events</c>. Les enfreindre est un bogue
    /// de l'appelant, pas un rejet métier ; en particulier, un solde que <c>NUMERIC(20,2)</c>
    /// arrondirait rendrait le hash invérifiable.
    /// </summary>
    private static void EnsureStorable(EventToAppend candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        if (candidate.BalanceAfter is { } balance && (decimal.Round(balance, 2) != balance || Math.Abs(balance) > MaxStoredAmount))
        {
            throw new ArgumentException($"Solde {balance} non représentable en NUMERIC(20,2).", nameof(candidate));
        }

        if (!DateOnly.TryParseExact(candidate.Period + "-01", "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var periodStart)
            || candidate.Period.Length != 7)
        {
            throw new ArgumentException($"Période « {candidate.Period} » invalide (format AAAA-MM).", nameof(candidate));
        }

        if (candidate.BusinessDate is { } date && (date.Year != periodStart.Year || date.Month != periodStart.Month))
        {
            throw new ArgumentException($"La date d'affaires {date:yyyy-MM-dd} n'appartient pas à la période {candidate.Period}.", nameof(candidate));
        }
    }
}
