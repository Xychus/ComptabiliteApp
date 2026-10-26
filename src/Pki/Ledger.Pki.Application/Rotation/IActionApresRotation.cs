namespace Ledger.Pki.Application.Rotation;

/// <summary>
/// Action déclenchée après la promotion d'un certificat (réimport ACM, redéploiement des tâches
/// ECS qui ne relisent pas leurs secrets…). Chaque action décide, d'après les étiquettes du
/// secret, si elle s'applique. Doit être idempotente.
/// </summary>
public interface IActionApresRotation
{
    string Nom { get; }

    bool SApplique(DescriptionSecret description);

    /// <param name="description">Secret renouvelé (étiquettes : cibles de l'action).</param>
    /// <param name="valeur">Nouvelle valeur courante du secret (JSON propre à son type).</param>
    /// <param name="cancellationToken">Annulation.</param>
    Task ExecuterAsync(DescriptionSecret description, string valeur, CancellationToken cancellationToken);
}
