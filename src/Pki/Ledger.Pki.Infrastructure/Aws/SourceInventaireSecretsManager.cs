using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Ledger.Pki.Application.Inventaire;
using Ledger.Pki.Application.Profils;
using Ledger.Pki.Application.Rotation;

namespace Ledger.Pki.Infrastructure.Aws;

/// <summary>Recense les secrets portant l'étiquette <c>pki:cn</c> et lit leur version courante.</summary>
public sealed class SourceInventaireSecretsManager(IAmazonSecretsManager secretsManager) : ISourceInventaire
{
    public async Task<IReadOnlyList<SecretRecense>> ListerAsync(CancellationToken cancellationToken)
    {
        var recenses = new List<SecretRecense>();
        string? suite = null;
        do
        {
            var page = await secretsManager.ListSecretsAsync(
                new ListSecretsRequest
                {
                    Filters = [new Filter { Key = FilterNameStringType.TagKey, Values = [ProfilCertificat.EtiquetteNomCommun] }],
                    NextToken = suite,
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var secret in page.SecretList ?? [])
            {
                var identite = (secret.Tags ?? []).FirstOrDefault(t => t.Key == ProfilCertificat.EtiquetteNomCommun)?.Value ?? secret.Name;
                recenses.Add(new SecretRecense(identite, await LireCouranteAsync(secret.ARN, cancellationToken).ConfigureAwait(false)));
            }

            suite = page.NextToken;
        }
        while (suite is not null);

        return recenses;
    }

    private async Task<string?> LireCouranteAsync(string secret, CancellationToken cancellationToken)
    {
        try
        {
            var reponse = await secretsManager.GetSecretValueAsync(
                new GetSecretValueRequest { SecretId = secret, VersionStage = EtiquettesVersion.Courante },
                cancellationToken).ConfigureAwait(false);
            return reponse.SecretString;
        }
        catch (ResourceNotFoundException)
        {
            return null;
        }
    }
}
