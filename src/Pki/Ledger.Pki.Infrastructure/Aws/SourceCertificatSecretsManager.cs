using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Rotation;
using Ledger.Pki.Application.Sentinelle;

namespace Ledger.Pki.Infrastructure.Aws;

/// <summary>Certificat courant d'une identité, lu dans son secret (rôle de tâche : lecture de ce seul secret).</summary>
public sealed class SourceCertificatSecretsManager(IAmazonSecretsManager secretsManager, string secret) : ISourceCertificatCourant
{
    public async Task<CertificatCourant> LireAsync(CancellationToken cancellationToken)
    {
        var reponse = await secretsManager.GetSecretValueAsync(
            new GetSecretValueRequest { SecretId = secret, VersionStage = EtiquettesVersion.Courante },
            cancellationToken).ConfigureAwait(false);
        var contenu = ContenuSecretCertificat.DepuisJson(reponse.SecretString ?? string.Empty)
            ?? throw new InvalidOperationException($"Le secret {secret} ne contient pas de certificat lisible.");
        return new CertificatCourant(reponse.VersionId, contenu);
    }

    public async Task<string> LireVersionAsync(CancellationToken cancellationToken)
    {
        var reponse = await secretsManager.DescribeSecretAsync(new DescribeSecretRequest { SecretId = secret }, cancellationToken).ConfigureAwait(false);
        return (reponse.VersionIdsToStages ?? []).FirstOrDefault(v => v.Value?.Contains(EtiquettesVersion.Courante) == true).Key
            ?? throw new InvalidOperationException($"Le secret {secret} n'a aucune version courante.");
    }
}
