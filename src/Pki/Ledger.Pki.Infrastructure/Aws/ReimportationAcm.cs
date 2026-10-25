using System.Text;
using Amazon.CertificateManager;
using Amazon.CertificateManager.Model;
using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Rotation;

namespace Ledger.Pki.Infrastructure.Aws;

/// <summary>
/// Réimporte le certificat dans ACM sous le même ARN (étiquette <c>pki:acm</c>), pour le load
/// balancer interne (ADR 0010). L'import est gratuit et l'ARN ne change pas : l'écouteur n'est pas
/// modifié.
/// </summary>
public sealed class ReimportationAcm(IAmazonCertificateManager acm) : IActionApresRotation
{
    public const string Etiquette = "pki:acm";

    public string Nom => "réimport ACM";

    public bool SApplique(DescriptionSecret description)
    {
        ArgumentNullException.ThrowIfNull(description);
        return description.Etiquettes.ContainsKey(Etiquette);
    }

    public async Task ExecuterAsync(DescriptionSecret description, ContenuSecretCertificat contenu, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(description);
        ArgumentNullException.ThrowIfNull(contenu);

        // Chaîne attendue par ACM : intermédiaire puis racine, sans le certificat final.
        var intermediaire = contenu.Chaine[contenu.Certificat.Length..].Trim();
        using var certificat = Flux(contenu.Certificat);
        using var cle = Flux(contenu.Cle);
        using var chaine = Flux($"{intermediaire}\n{contenu.Racine}");
        await acm.ImportCertificateAsync(
            new ImportCertificateRequest
            {
                CertificateArn = description.Etiquettes[Etiquette],
                Certificate = certificat,
                PrivateKey = cle,
                CertificateChain = chaine,
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static MemoryStream Flux(string pem) => new(Encoding.ASCII.GetBytes(pem));
}
