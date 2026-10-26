using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ledger.Postgres.Infrastructure;

/// <summary>Réglage appliqué à la construction de la source de données (authentification, etc.).</summary>
public interface IConfigurationSourceDonnees
{
    void Configurer(NpgsqlDataSourceBuilder constructeur);
}

/// <summary>
/// Construit la source de données PostgreSQL d'un service à partir de sa chaîne de connexion,
/// en appliquant les réglages enregistrés (ex. <see cref="AuthentificationIamRds"/> en AWS). Sans
/// réglage, équivaut à <see cref="NpgsqlDataSource.Create(string)"/> (local, tests).
/// </summary>
public static class SourceDonneesPostgres
{
    public static NpgsqlDataSource Creer(string chaineConnexion, IEnumerable<IConfigurationSourceDonnees> configurations)
    {
        ArgumentNullException.ThrowIfNull(configurations);

        var constructeur = new NpgsqlDataSourceBuilder(chaineConnexion);
        foreach (var configuration in configurations)
        {
            configuration.Configurer(constructeur);
        }

        return constructeur.Build();
    }

    /// <summary>Source de données du fournisseur de services, avec les réglages qui y sont enregistrés.</summary>
    public static NpgsqlDataSource Creer(IServiceProvider services, string chaineConnexion) =>
        Creer(chaineConnexion, services.GetServices<IConfigurationSourceDonnees>());
}
