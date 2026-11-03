using Amazon;
using Amazon.RDS.Util;
using Amazon.Runtime;
using Amazon.Runtime.Credentials;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ledger.Postgres.Infrastructure;

/// <summary>
/// Authentification IAM à Aurora (ADR 0029) : le mot de passe est un jeton signé par le rôle de
/// la tâche, valable 15 minutes, renouvelé toutes les 10 minutes. Aucun mot de passe n'est stocké
/// ni à renouveler ; le rôle PostgreSQL doit être membre de <c>rds_iam</c> (migration 0009).
/// TLS obligatoire : la chaîne de connexion porte <c>SSL Mode=VerifyFull</c>.
/// </summary>
public sealed class AuthentificationIamRds(RegionEndpoint region, AWSCredentials? identifiants = null) : IConfigurationSourceDonnees
{
    public static readonly TimeSpan Renouvellement = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan NouvelleTentative = TimeSpan.FromSeconds(5);

    public void Configurer(NpgsqlDataSourceBuilder constructeur)
    {
        ArgumentNullException.ThrowIfNull(constructeur);

        if (constructeur.ConnectionStringBuilder.SslMode != SslMode.VerifyFull)
        {
            throw new InvalidOperationException("L'authentification IAM exige SSL Mode=VerifyFull.");
        }

        constructeur.UsePeriodicPasswordProvider(
            (reglages, _) => ValueTask.FromResult(Jeton(reglages.Host!, reglages.Port, reglages.Username!)),
            Renouvellement,
            NouvelleTentative);
    }

    public string Jeton(string hote, int port, string utilisateur) =>
        RDSAuthTokenGenerator.GenerateAuthToken(identifiants ?? DefaultAWSCredentialsIdentityResolver.GetCredentials(), region, hote, port, utilisateur);
}

/// <summary>Options de connexion à Aurora (section <c>Ledger:Postgres</c>).</summary>
public sealed class OptionsPostgres
{
    public const string Section = "Ledger:Postgres";

    /// <summary>Vrai en AWS : authentification IAM du rôle de la tâche.</summary>
    public bool AuthentificationIam { get; set; }

    /// <summary>Région d'Aurora (ex. <c>ca-central-1</c>).</summary>
    public string? Region { get; set; }
}

public static class PostgresServiceCollectionExtensions
{
    /// <summary>Active l'authentification IAM si <see cref="OptionsPostgres.AuthentificationIam"/> est vrai ; sinon ne fait rien.</summary>
    public static IServiceCollection AjouterAuthentificationPostgres(this IServiceCollection services, OptionsPostgres options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.AuthentificationIam)
        {
            return services;
        }

        if (string.IsNullOrWhiteSpace(options.Region))
        {
            throw new InvalidOperationException("Ledger:Postgres:Region est obligatoire avec l'authentification IAM.");
        }

        services.AddSingleton<IConfigurationSourceDonnees>(new AuthentificationIamRds(RegionEndpoint.GetBySystemName(options.Region)));
        return services;
    }
}
