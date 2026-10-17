using Ledger.SharedKernel;

namespace Ledger.Command.Application.EventStore;

/// <summary>Lecture du journal, flux par flux.</summary>
public interface IEventReader
{
    /// <summary>Événements du flux par version croissante ; liste vide si le flux n'existe pas.</summary>
    Task<IReadOnlyList<StoredEvent>> ReadStreamAsync(string streamId, CancellationToken cancellationToken);
}

/// <summary>Prépare des événements dans l'unité de travail courante ; rien n'est écrit avant <see cref="IUnitOfWork.CommitAsync"/>.</summary>
public interface IEventAppender
{
    void Append(EventToAppend eventToAppend);
}

/// <summary>
/// Valide atomiquement les événements préparés et leurs lignes d'outbox : tout est écrit, ou rien.
/// Les événements préparés sont abandonnés après l'appel, quel qu'en soit le résultat.
/// </summary>
public interface IUnitOfWork
{
    /// <returns>
    /// Les événements stockés, dans l'ordre de préparation ; ou un rejet
    /// <see cref="EventStoreErrors.ConcurrencyConflictCode"/>, <see cref="EventStoreErrors.EntryIdAlreadyUsedCode"/>
    /// ou <see cref="EventStoreErrors.PeriodSealedCode"/>.
    /// </returns>
    Task<Result<IReadOnlyList<StoredEvent>>> CommitAsync(CancellationToken cancellationToken);
}
