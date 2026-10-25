using Ledger.Pki.Application.Emission;

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

    Task ExecuterAsync(DescriptionSecret description, ContenuSecretCertificat contenu, CancellationToken cancellationToken);
}
