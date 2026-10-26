using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Ledger.Pki.Application.Rotation;

namespace Ledger.Pki.Infrastructure.Aws;

/// <summary>Magasin des certificats dans Secrets Manager (secrets chiffrés par la CMK <c>secrets</c>).</summary>
public sealed class MagasinCertificatsSecretsManager(IAmazonSecretsManager secretsManager) : IMagasinCertificats
{
    public async Task<DescriptionSecret> DecrireAsync(string secret, CancellationToken cancellationToken)
    {
        var reponse = await secretsManager.DescribeSecretAsync(new DescribeSecretRequest { SecretId = secret }, cancellationToken).ConfigureAwait(false);

        // SDK v4 : une collection vide est renvoyée à null.
        var versions = (reponse.VersionIdsToStages ?? [])
            .ToDictionary(v => v.Key, v => (IReadOnlyList<string>)(v.Value ?? []), StringComparer.Ordinal);
        var etiquettes = (reponse.Tags ?? [])
            .ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal);
        return new DescriptionSecret(reponse.ARN ?? secret, reponse.RotationEnabled ?? false, versions, etiquettes);
    }

    public Task<string?> LireParEtiquetteAsync(string secret, string etiquette, CancellationToken cancellationToken) =>
        LireAsync(new GetSecretValueRequest { SecretId = secret, VersionStage = etiquette }, cancellationToken);

    public Task<string?> LireVersionAsync(string secret, string version, CancellationToken cancellationToken) =>
        LireAsync(new GetSecretValueRequest { SecretId = secret, VersionId = version }, cancellationToken);

    public Task EcrireEnAttenteAsync(string secret, string version, string valeur, CancellationToken cancellationToken) =>
        secretsManager.PutSecretValueAsync(
            new PutSecretValueRequest
            {
                SecretId = secret,
                ClientRequestToken = version,
                SecretString = valeur,
                VersionStages = [EtiquettesVersion.EnAttente],
            },
            cancellationToken);

    public Task PromouvoirAsync(string secret, string version, string? versionCourante, CancellationToken cancellationToken) =>
        secretsManager.UpdateSecretVersionStageAsync(
            new UpdateSecretVersionStageRequest
            {
                SecretId = secret,
                VersionStage = EtiquettesVersion.Courante,
                MoveToVersionId = version,
                RemoveFromVersionId = versionCourante,
            },
            cancellationToken);

    private async Task<string?> LireAsync(GetSecretValueRequest requete, CancellationToken cancellationToken)
    {
        try
        {
            var reponse = await secretsManager.GetSecretValueAsync(requete, cancellationToken).ConfigureAwait(false);
            return reponse.SecretString;
        }
        catch (ResourceNotFoundException)
        {
            return null;
        }
    }
}
