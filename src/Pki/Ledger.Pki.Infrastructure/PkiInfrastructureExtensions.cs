using Amazon.CertificateManager;
using Amazon.CloudWatch;
using Amazon.ECS;
using Amazon.KeyManagementService;
using Amazon.SecretsManager;
using Ledger.Pki.Application.Emission;
using Ledger.Pki.Application.Inventaire;
using Ledger.Pki.Application.MotsDePasse;
using Ledger.Pki.Application.Rotation;
using Ledger.Pki.Application.Sentinelle;
using Ledger.Pki.Infrastructure.Aws;
using Ledger.Pki.Infrastructure.Fichiers;
using Ledger.Pki.Infrastructure.Kafka;
using Ledger.Pki.Infrastructure.Postgres;
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

    /// <summary>
    /// Administration des rôles PostgreSQL pour la rotation des mots de passe (Debezium) : rôle
    /// maître lu dans son secret géré par RDS. À appeler après <see cref="AjouterPkiAws"/>.
    /// </summary>
    public static IServiceCollection AjouterAdministrationPostgresAws(this IServiceCollection services, OptionsAdministrationPostgres options, string secretMaitre)
    {
        services.AddSingleton<ISourceIdentifiantsMaitre>(sp => new IdentifiantsMaitreSecretsManager(sp.GetRequiredService<IAmazonSecretsManager>(), secretMaitre));
        services.AddSingleton<IAdministrationRolesPostgres>(sp => new AdministrationRolesNpgsql(options, sp.GetRequiredService<ISourceIdentifiantsMaitre>()));
        return services;
    }

    /// <summary>
    /// Adaptateurs de la sentinelle d'un broker : secret du nœud, fichiers PEM, santé et mesures de
    /// la grappe (volume de données monté en lecture, groupes de consommateurs surveillés).
    /// </summary>
    public static IServiceCollection AjouterSentinelleAws(
        this IServiceCollection services, string secret, string dossier, string serveurs, int nombreNoeuds, string dossierDonnees, IReadOnlyList<string> groupes)
    {
        services.AddSingleton<IAmazonSecretsManager>(_ => new AmazonSecretsManagerClient());
        services.AddSingleton<IAmazonCloudWatch>(_ => new AmazonCloudWatchClient());
        services.AddSingleton<ISourceCertificatCourant>(sp => new SourceCertificatSecretsManager(sp.GetRequiredService<IAmazonSecretsManager>(), secret));
        services.AddSingleton<IEcrivainCertificats>(new EcrivainCertificatsFichiers(dossier));
        services.AddSingleton<ISanteGrappe>(new SanteGrappeKafka(serveurs, nombreNoeuds));
        services.AddSingleton<ICollecteMesuresGrappe>(new CollecteMesuresKafka(serveurs, dossierDonnees, groupes));
        services.AddSingleton<IPublicateurMesuresGrappe, PublicateurMesuresGrappeCloudWatch>();
        return services;
    }
}
