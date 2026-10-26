using System.Text.Json;
using System.Text.Json.Serialization;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Ledger.Pki.Application.MotsDePasse;
using Npgsql;

namespace Ledger.Pki.Infrastructure.Postgres;

/// <summary>Identifiants du rôle maître d'Aurora.</summary>
public sealed record IdentifiantsMaitre(string Utilisateur, string MotDePasse)
{
    public override string ToString() => $"IdentifiantsMaitre {{ Utilisateur = {Utilisateur} }}";
}

/// <summary>Source des identifiants du rôle maître (relus à chaque usage : RDS les renouvelle tous les 7 jours).</summary>
public interface ISourceIdentifiantsMaitre
{
    Task<IdentifiantsMaitre> LireAsync(CancellationToken cancellationToken);
}

/// <summary>Secret maître géré par RDS (JSON <c>username</c>, <c>password</c> : format imposé par RDS).</summary>
public sealed class IdentifiantsMaitreSecretsManager(IAmazonSecretsManager secretsManager, string secret) : ISourceIdentifiantsMaitre
{
    public async Task<IdentifiantsMaitre> LireAsync(CancellationToken cancellationToken)
    {
        var reponse = await secretsManager.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secret }, cancellationToken).ConfigureAwait(false);
        var valeur = JsonSerializer.Deserialize<SecretRds>(reponse.SecretString ?? "{}");
        return valeur is { Username.Length: > 0, Password.Length: > 0 }
            ? new IdentifiantsMaitre(valeur.Username, valeur.Password)
            : throw new InvalidOperationException($"Le secret maître {secret} est illisible.");
    }

    private sealed record SecretRds(
        [property: JsonPropertyName("username")] string? Username,
        [property: JsonPropertyName("password")] string? Password);
}

/// <summary>Emplacement d'Aurora pour l'administration des rôles (TLS VerifyFull contre le certificat racine RDS de l'image).</summary>
public sealed record OptionsAdministrationPostgres(string Hote, string Base, string? CertificatRacine, int Port = 5432);

/// <summary>
/// Administration des rôles par Npgsql : <c>ALTER ROLE … LOGIN PASSWORD</c> avec un vérificateur
/// SCRAM-SHA-256 (jamais le mot de passe en clair dans les journaux d'Aurora), test de connexion
/// du rôle. Le nom du rôle est cité côté serveur (<c>format('%I')</c>).
/// </summary>
public sealed class AdministrationRolesNpgsql(OptionsAdministrationPostgres options, ISourceIdentifiantsMaitre maitre) : IAdministrationRolesPostgres
{
    public async Task DefinirMotDePasseAsync(string role, string motDePasse, CancellationToken cancellationToken)
    {
        var identifiants = await maitre.LireAsync(cancellationToken).ConfigureAwait(false);
        var connexion = new NpgsqlConnection(Chaine(identifiants.Utilisateur, identifiants.MotDePasse));
        await using (connexion.ConfigureAwait(false))
        {
            await connexion.OpenAsync(cancellationToken).ConfigureAwait(false);

            string instruction;
            var formatage = new NpgsqlCommand("SELECT format('ALTER ROLE %I WITH LOGIN PASSWORD %L', $1::text, $2::text)", connexion);
            await using (formatage.ConfigureAwait(false))
            {
                formatage.Parameters.Add(new NpgsqlParameter { Value = role });
                formatage.Parameters.Add(new NpgsqlParameter { Value = VerificateurScram.Calculer(motDePasse) });
                instruction = (string)(await formatage.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
            }

#pragma warning disable CA2100 // Instruction produite par format() côté serveur : identifiant et littéral cités par PostgreSQL.
            var modification = new NpgsqlCommand(instruction, connexion);
#pragma warning restore CA2100
            await using (modification.ConfigureAwait(false))
            {
                await modification.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<bool> TesterConnexionAsync(string role, string motDePasse, CancellationToken cancellationToken)
    {
        try
        {
            var connexion = new NpgsqlConnection(Chaine(role, motDePasse));
            await using (connexion.ConfigureAwait(false))
            {
                await connexion.OpenAsync(cancellationToken).ConfigureAwait(false);
                var commande = new NpgsqlCommand("SELECT 1", connexion);
                await using (commande.ConfigureAwait(false))
                {
                    return (int)(await commande.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! == 1;
                }
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidPassword)
        {
            return false;
        }
    }

    private string Chaine(string utilisateur, string motDePasse)
    {
        var constructeur = new NpgsqlConnectionStringBuilder
        {
            Host = options.Hote,
            Port = options.Port,
            Database = options.Base,
            Username = utilisateur,
            Password = motDePasse,
            Pooling = false,
        };
        if (options.CertificatRacine is not null)
        {
            constructeur.SslMode = SslMode.VerifyFull;
            constructeur.RootCertificate = options.CertificatRacine;
        }

        return constructeur.ConnectionString;
    }
}
