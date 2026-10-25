using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Profils;
using Ledger.SharedKernel;

namespace Ledger.Pki.Application.Rotation;

/// <summary><c>createSecret</c> : émet un nouveau certificat et l'enregistre en attente (idempotent).</summary>
public sealed class CreationSecret(IMagasinCertificats magasin, EmetteurCertificats emetteur) : IEtapeRotation
{
    public string TypeSecret => TypesSecret.Certificat;

    public EtapeRotation Etape => EtapeRotation.CreerSecret;

    public async Task<Result<string>> ExecuterAsync(DemandeRotation demande, DescriptionSecret description, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demande);
        ArgumentNullException.ThrowIfNull(description);

        if (await magasin.LireVersionAsync(demande.Secret, demande.Version, cancellationToken).ConfigureAwait(false) is not null)
        {
            return Result.Success($"Version {demande.Version} déjà créée.");
        }

        var profil = ProfilCertificat.DepuisEtiquettes(description.Etiquettes);
        if (profil.IsFailure)
        {
            return Result.Failure<string>(profil.Error);
        }

        var emission = await emetteur.EmettreAsync(profil.Value, cancellationToken).ConfigureAwait(false);
        if (emission.IsFailure)
        {
            return Result.Failure<string>(emission.Error);
        }

        await magasin.EcrireEnAttenteAsync(demande.Secret, demande.Version, emission.Value.VersJson(), cancellationToken).ConfigureAwait(false);
        return Result.Success($"Certificat émis, échéance {emission.Value.ExpireLe:O}.");
    }
}

/// <summary><c>setSecret</c> : rien à pousser ailleurs avant le test (l'import ACM suit la promotion).</summary>
public sealed class DefinitionSecret : IEtapeRotation
{
    public string TypeSecret => TypesSecret.Certificat;

    public EtapeRotation Etape => EtapeRotation.DefinirSecret;

    public Task<Result<string>> ExecuterAsync(DemandeRotation demande, DescriptionSecret description, CancellationToken cancellationToken) =>
        Task.FromResult(Result.Success("Aucune action."));
}

/// <summary><c>testSecret</c> : vérifie le certificat en attente avant sa promotion.</summary>
public sealed class TestSecret(IMagasinCertificats magasin, VerificateurCertificat verificateur) : IEtapeRotation
{
    public string TypeSecret => TypesSecret.Certificat;

    public EtapeRotation Etape => EtapeRotation.TesterSecret;

    public async Task<Result<string>> ExecuterAsync(DemandeRotation demande, DescriptionSecret description, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demande);
        ArgumentNullException.ThrowIfNull(description);

        var valeur = await magasin.LireVersionAsync(demande.Secret, demande.Version, cancellationToken).ConfigureAwait(false);
        if (valeur is null || ContenuSecretCertificat.DepuisJson(valeur) is not { } contenu)
        {
            return Result.Failure<string>(new DomainError("pki.rotation.contenu", $"Version {demande.Version} absente ou illisible."));
        }

        return ProfilCertificat.DepuisEtiquettes(description.Etiquettes)
            .Bind(profil => verificateur.Verifier(contenu, profil))
            .Map(c => $"Certificat valide jusqu'au {c.ExpireLe:O}.");
    }
}

/// <summary>
/// <c>finishSecret</c> : promeut la version, puis exécute les actions qui suivent la rotation.
/// Les actions sont rejouées si l'étape l'est (nouvelle tentative après un échec) : elles sont
/// idempotentes.
/// </summary>
public sealed class FinSecret(IMagasinCertificats magasin, IEnumerable<IActionApresRotation> actions, string typeSecret = TypesSecret.Certificat) : IEtapeRotation
{
    public string TypeSecret => typeSecret;

    public EtapeRotation Etape => EtapeRotation.TerminerSecret;

    public async Task<Result<string>> ExecuterAsync(DemandeRotation demande, DescriptionSecret description, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(demande);
        ArgumentNullException.ThrowIfNull(description);

        var courante = description.VersionPortant(EtiquettesVersion.Courante);
        if (courante != demande.Version)
        {
            await magasin.PromouvoirAsync(demande.Secret, demande.Version, courante, cancellationToken).ConfigureAwait(false);
        }

        var valeur = await magasin.LireVersionAsync(demande.Secret, demande.Version, cancellationToken).ConfigureAwait(false);
        if (valeur is null)
        {
            return Result.Failure<string>(new DomainError("pki.rotation.contenu", $"Version {demande.Version} absente."));
        }

        var executees = new List<string>();
        foreach (var action in actions.Where(a => a.SApplique(description)))
        {
            await action.ExecuterAsync(description, valeur, cancellationToken).ConfigureAwait(false);
            executees.Add(action.Nom);
        }

        return Result.Success(executees.Count == 0
            ? $"Version {demande.Version} courante."
            : $"Version {demande.Version} courante ; actions : {string.Join(", ", executees)}.");
    }
}
