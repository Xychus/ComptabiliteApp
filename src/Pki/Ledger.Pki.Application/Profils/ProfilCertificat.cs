using Ledger.SharedKernel;

namespace Ledger.Pki.Application.Profils;

/// <summary>Usages étendus (EKU) d'un certificat final.</summary>
[Flags]
public enum UsageCertificat
{
    Aucun = 0,

    /// <summary>Authentification du serveur TLS (brokers, Kestrel, load balancer).</summary>
    Serveur = 1,

    /// <summary>Authentification du client TLS (clients Kafka, mTLS entre brokers).</summary>
    Client = 2,
}

/// <summary>
/// Identité d'un certificat final (ADR 0010) : nom commun (identité des ACL Kafka après
/// <c>ssl.principal.mapping.rules</c>), noms DNS (SAN) et usages.
/// </summary>
public sealed record ProfilCertificat(string NomCommun, IReadOnlyList<string> NomsDns, UsageCertificat Usages)
{
    /// <summary>Étiquette du secret portant le nom commun.</summary>
    public const string EtiquetteNomCommun = "pki:cn";

    /// <summary>Étiquette portant les noms DNS, séparés par des espaces (la virgule est interdite dans une étiquette AWS).</summary>
    public const string EtiquetteNomsDns = "pki:san";

    /// <summary>Étiquette portant l'usage : <c>serveur</c>, <c>client</c> ou <c>serveur-client</c>.</summary>
    public const string EtiquetteUsage = "pki:usage";

    private static readonly char[] _separateurs = [' '];

    /// <summary>
    /// Lit le profil dans les étiquettes du secret, posées par Terraform (module <c>pki</c>) : la
    /// Lambda n'a aucune configuration propre à une identité.
    /// </summary>
    public static Result<ProfilCertificat> DepuisEtiquettes(IReadOnlyDictionary<string, string> etiquettes)
    {
        ArgumentNullException.ThrowIfNull(etiquettes);

        if (!etiquettes.TryGetValue(EtiquetteNomCommun, out var nomCommun) || !EstNomValide(nomCommun))
        {
            return Echec("pki.profil.nom-commun", $"Étiquette « {EtiquetteNomCommun} » absente ou invalide.");
        }

        var nomsDns = etiquettes.TryGetValue(EtiquetteNomsDns, out var san)
            ? san.Split(_separateurs, StringSplitOptions.RemoveEmptyEntries)
            : [];
        if (nomsDns.FirstOrDefault(nom => !EstNomValide(nom)) is { } fautif)
        {
            return Echec("pki.profil.nom-dns", $"Nom DNS invalide dans « {EtiquetteNomsDns} » : {fautif}.");
        }

        var usages = etiquettes.GetValueOrDefault(EtiquetteUsage) switch
        {
            "serveur" => UsageCertificat.Serveur,
            "client" => UsageCertificat.Client,
            "serveur-client" => UsageCertificat.Serveur | UsageCertificat.Client,
            _ => UsageCertificat.Aucun,
        };
        if (usages == UsageCertificat.Aucun)
        {
            return Echec("pki.profil.usage", $"Étiquette « {EtiquetteUsage} » absente ou invalide (serveur, client, serveur-client).");
        }

        if (usages.HasFlag(UsageCertificat.Serveur) && nomsDns.Length == 0)
        {
            return Echec("pki.profil.nom-dns", "Un certificat serveur exige au moins un nom DNS.");
        }

        return Result.Success(new ProfilCertificat(nomCommun, nomsDns, usages));
    }

    // Lettres, chiffres, tirets et points ; ni caractère spécial ni joker (aucun certificat « *. »).
    private static bool EstNomValide(string nom) =>
        nom.Length is > 0 and <= 253
        && nom.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.')
        && !nom.StartsWith('.') && !nom.EndsWith('.') && !nom.StartsWith('-');

    private static Result<ProfilCertificat> Echec(string code, string message) =>
        Result.Failure<ProfilCertificat>(new DomainError(code, message));
}
