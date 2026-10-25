using Amazon;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Ledger.Pki.Infrastructure.Memoire;

namespace Ledger.Pki.Infrastructure.Tests.Contrats;

/// <summary>
/// Client Secrets Manager simulé, adossé au magasin en mémoire (aucun appel réseau) : vérifie la
/// traduction des requêtes et réponses du SDK par les adaptateurs, y compris la pagination et
/// les collections vides renvoyées à <see langword="null"/> par le SDK v4.
/// </summary>
internal sealed class SecretsManagerEssai(MagasinCertificatsMemoire memoire, int taillePage = 1)
    : AmazonSecretsManagerClient(new AnonymousAWSCredentials(), RegionEndpoint.CACentral1)
{
    public List<string> Secrets { get; } = [];

    public override async Task<DescribeSecretResponse> DescribeSecretAsync(DescribeSecretRequest request, CancellationToken cancellationToken = default)
    {
        var d = await memoire.DecrireAsync(request.SecretId, cancellationToken);
        return new DescribeSecretResponse
        {
            ARN = d.Identifiant,
            RotationEnabled = d.RotationActivee ? true : null,
            VersionIdsToStages = d.EtiquettesParVersion.Count == 0 ? null : d.EtiquettesParVersion.ToDictionary(v => v.Key, v => v.Value.ToList()),
            Tags = d.Etiquettes.Count == 0 ? null : [.. d.Etiquettes.Select(e => new Tag { Key = e.Key, Value = e.Value })],
        };
    }

    public override async Task<GetSecretValueResponse> GetSecretValueAsync(GetSecretValueRequest request, CancellationToken cancellationToken = default)
    {
        var valeur = request.VersionId is { } version
            ? await memoire.LireVersionAsync(request.SecretId, version, cancellationToken)
            : await memoire.LireParEtiquetteAsync(request.SecretId, request.VersionStage ?? "AWSCURRENT", cancellationToken);
        if (valeur is null)
        {
            throw new ResourceNotFoundException("Secrets Manager can't find the specified secret value.");
        }

        var versionLue = request.VersionId ?? (await memoire.DecrireAsync(request.SecretId, cancellationToken)).VersionPortant(request.VersionStage ?? "AWSCURRENT");
        return new GetSecretValueResponse { SecretString = valeur, VersionId = versionLue };
    }

    public override async Task<PutSecretValueResponse> PutSecretValueAsync(PutSecretValueRequest request, CancellationToken cancellationToken = default)
    {
        Assert.Equal(["AWSPENDING"], request.VersionStages);
        await memoire.EcrireEnAttenteAsync(request.SecretId, request.ClientRequestToken, request.SecretString, cancellationToken);
        return new PutSecretValueResponse();
    }

    public override async Task<UpdateSecretVersionStageResponse> UpdateSecretVersionStageAsync(UpdateSecretVersionStageRequest request, CancellationToken cancellationToken = default)
    {
        Assert.Equal("AWSCURRENT", request.VersionStage);
        await memoire.PromouvoirAsync(request.SecretId, request.MoveToVersionId, request.RemoveFromVersionId, cancellationToken);
        return new UpdateSecretVersionStageResponse();
    }

    public override Task<ListSecretsResponse> ListSecretsAsync(ListSecretsRequest request, CancellationToken cancellationToken = default)
    {
        var filtre = Assert.Single(request.Filters);
        Assert.Equal(FilterNameStringType.TagKey, filtre.Key);
        Assert.Equal(["pki:cn"], filtre.Values);

        var debut = request.NextToken is { } jeton ? int.Parse(jeton, System.Globalization.CultureInfo.InvariantCulture) : 0;
        var page = Secrets.Skip(debut).Take(taillePage).ToList();
        var suite = debut + page.Count < Secrets.Count ? (debut + page.Count).ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        var entrees = new List<SecretListEntry>();
        foreach (var nom in page)
        {
            var etiquettes = memoire.DecrireAsync(nom, cancellationToken).Result.Etiquettes;
            entrees.Add(new SecretListEntry { ARN = nom, Name = nom, Tags = [.. etiquettes.Select(e => new Tag { Key = e.Key, Value = e.Value })] });
        }

        return Task.FromResult(new ListSecretsResponse { SecretList = entrees.Count == 0 ? null : entrees, NextToken = suite });
    }
}
