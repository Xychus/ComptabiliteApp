using Amazon.CertificateManager;
using Amazon.CloudWatch;
using Amazon.ECS;
using Amazon.KeyManagementService;
using Amazon.SecretsManager;
using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Inventaire;
using Ledger.Pki.Application.Rotation;
using Ledger.Pki.Infrastructure.Aws;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Pki.Infrastructure;

/// <summary>Enregistrement des adaptateurs AWS de la PKI (région et identifiants : environnement de la Lambda).</summary>
public static class PkiInfrastructureExtensions
{
    public static IServiceCollection AjouterPkiAws(this IServiceCollection services)
    {
        services.AddSingleton<IAmazonKeyManagementService>(_ => new AmazonKeyManagementServiceClient());
        services.AddSingleton<IAmazonSecretsManager>(_ => new AmazonSecretsManagerClient());
        services.AddSingleton<IAmazonCertificateManager>(_ => new AmazonCertificateManagerClient());
        services.AddSingleton<IAmazonECS>(_ => new AmazonECSClient());
        services.AddSingleton<IAmazonCloudWatch>(_ => new AmazonCloudWatchClient());

        services.AddSingleton<ISignataireAutorite, SignataireKms>();
        services.AddSingleton<IMagasinCertificats, MagasinCertificatsSecretsManager>();
        services.AddSingleton<ISourceInventaire, SourceInventaireSecretsManager>();
        services.AddSingleton<IPublicateurMesures, PublicateurMesuresCloudWatch>();
        services.AddSingleton<IActionApresRotation, ReimportationAcm>();
        services.AddSingleton<IActionApresRotation, RedeploiementEcs>();
        return services;
    }
}
