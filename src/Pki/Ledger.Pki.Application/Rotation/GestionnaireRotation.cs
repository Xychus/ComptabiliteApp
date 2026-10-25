using Ledger.SharedKernel;

namespace Ledger.Pki.Application.Rotation;

/// <summary>Étapes du protocole de rotation de Secrets Manager (champ <c>Step</c> de l'événement).</summary>
public enum EtapeRotation
{
    CreerSecret,
    DefinirSecret,
    TesterSecret,
    TerminerSecret,
}

/// <summary>Demande de rotation reçue de Secrets Manager : secret, jeton (identifiant de la nouvelle version), étape.</summary>
public sealed record DemandeRotation(string Secret, string Version, EtapeRotation Etape);

/// <summary>Une étape du protocole ; une implémentation par étape (ajout, pas de <c>switch</c>).</summary>
public interface IEtapeRotation
{
    EtapeRotation Etape { get; }

    Task<Result<string>> ExecuterAsync(DemandeRotation demande, DescriptionSecret description, CancellationToken cancellationToken);
}

/// <summary>
/// Point d'entrée de la rotation : vérifie les préconditions communes du protocole (rotation
/// activée, version connue et dans le bon état) puis délègue à l'étape demandée.
/// </summary>
public sealed class GestionnaireRotation(IMagasinCertificats magasin, IEnumerable<IEtapeRotation> etapes)
{
    private readonly Dictionary<EtapeRotation, IEtapeRotation> _etapes = etapes.ToDictionary(e => e.Etape);

    public async Task<Result<string>> TraiterAsync(DemandeRotation demande, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demande);

        var description = await magasin.DecrireAsync(demande.Secret, cancellationToken).ConfigureAwait(false);
        if (!description.RotationActivee)
        {
            return Echec("pki.rotation.inactive", $"La rotation n'est pas activée sur {demande.Secret}.");
        }

        if (!description.EtiquettesParVersion.ContainsKey(demande.Version))
        {
            return Echec("pki.rotation.version-inconnue", $"La version {demande.Version} n'existe pas dans {demande.Secret}.");
        }

        // Fin de rotation déjà faite : Secrets Manager peut rejouer une étape (nouvelle tentative).
        if (demande.Etape != EtapeRotation.TerminerSecret && description.VersionPorte(demande.Version, EtiquettesVersion.Courante))
        {
            return Result.Success($"La version {demande.Version} est déjà courante.");
        }

        if (demande.Etape != EtapeRotation.TerminerSecret && !description.VersionPorte(demande.Version, EtiquettesVersion.EnAttente))
        {
            return Echec("pki.rotation.etat", $"La version {demande.Version} n'est pas en attente.");
        }

        return _etapes.TryGetValue(demande.Etape, out var etape)
            ? await etape.ExecuterAsync(demande, description, cancellationToken).ConfigureAwait(false)
            : Echec("pki.rotation.etape", $"Étape non prise en charge : {demande.Etape}.");
    }

    private static Result<string> Echec(string code, string message) => Result.Failure<string>(new DomainError(code, message));
}
