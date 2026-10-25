using Ledger.Pki.Application.Inventaire;
using Ledger.Pki.Application.Rotation;
using Ledger.SharedKernel;

namespace Ledger.Pki.Lambda;

/// <summary>
/// Opérations qui exigent une intermédiaire installée (rotation, inventaire). Avant la première
/// cérémonie, la Lambda ne sait que produire une demande de signature.
/// </summary>
public interface IOperationsAutorite
{
    Task<Result<string>> TournerAsync(DemandeRotation demande, CancellationToken cancellationToken);

    Task<IReadOnlyList<MesureEcheance>> InventorierAsync(CancellationToken cancellationToken);
}

/// <summary>Intermédiaire installée : délègue aux services applicatifs.</summary>
public sealed class OperationsAutorite(GestionnaireRotation rotation, InventaireEcheances inventaire) : IOperationsAutorite
{
    public Task<Result<string>> TournerAsync(DemandeRotation demande, CancellationToken cancellationToken) =>
        rotation.TraiterAsync(demande, cancellationToken);

    public Task<IReadOnlyList<MesureEcheance>> InventorierAsync(CancellationToken cancellationToken) =>
        inventaire.ExecuterAsync(cancellationToken);
}

/// <summary>Mode cérémonie (aucune intermédiaire installée) : toute rotation échoue explicitement.</summary>
public sealed class AutoriteNonInstallee : IOperationsAutorite
{
    private const string Message =
        "Aucune intermédiaire installée : terminez la cérémonie (runbook pki-ceremonie-annuelle) avant toute rotation.";

    public Task<Result<string>> TournerAsync(DemandeRotation demande, CancellationToken cancellationToken) =>
        throw new EchecPkiException(Message);

    public Task<IReadOnlyList<MesureEcheance>> InventorierAsync(CancellationToken cancellationToken) =>
        throw new EchecPkiException(Message);
}
