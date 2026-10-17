namespace Ledger.Command.Domain;

/// <summary>
/// Fait métier accompli, émis par une décision. Les champs d'autorité serveur
/// (séquence globale, horodatage, hash) sont ajoutés par l'event store, pas par le domaine.
/// </summary>
public interface IDomainEvent;
