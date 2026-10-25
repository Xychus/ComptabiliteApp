using Ledger.Pki.Application.Emission;
using Ledger.SharedKernel;

namespace Ledger.Pki.Application.Inventaire;

/// <summary>Secret de certificat recensé : identité (nom commun) et valeur courante, absente si jamais émise.</summary>
public sealed record SecretRecense(string Identite, string? ValeurCourante);

/// <summary>Port de recensement des secrets de certificats finaux (ceux qui portent l'étiquette <c>pki:cn</c>).</summary>
public interface ISourceInventaire
{
    Task<IReadOnlyList<SecretRecense>> ListerAsync(CancellationToken cancellationToken);
}

/// <summary>Nombre de jours avant l'échéance d'une identité (0 si échue, jamais émise ou illisible).</summary>
public sealed record MesureEcheance(string Identite, double JoursRestants);

/// <summary>Port de publication des mesures (métriques CloudWatch en production).</summary>
public interface IPublicateurMesures
{
    Task PublierAsync(IReadOnlyList<MesureEcheance> mesures, CancellationToken cancellationToken);
}

/// <summary>
/// Inventaire quotidien (ADR 0010) : publie le nombre de jours avant l'échéance de chaque
/// certificat final et de l'intermédiaire, ainsi que le minimum des certificats finaux, sur
/// lequel portent les alarmes. Un échec de rotation non détecté couperait le trafic.
/// </summary>
public sealed class InventaireEcheances(ISourceInventaire source, IPublicateurMesures publicateur, AutoriteIntermediaire autorite, IClock horloge)
{
    /// <summary>Identité de la mesure de l'intermédiaire.</summary>
    public const string IdentiteIntermediaire = "ac-intermediaire";

    /// <summary>Identité de la mesure du minimum sur les certificats finaux.</summary>
    public const string IdentiteMinimum = "minimum-certificats-finaux";

    public async Task<IReadOnlyList<MesureEcheance>> ExecuterAsync(CancellationToken cancellationToken)
    {
        var maintenant = horloge.UtcNow;
        var secrets = await source.ListerAsync(cancellationToken).ConfigureAwait(false);

        var mesures = secrets
            .Select(s => new MesureEcheance(s.Identite, JoursRestants(s.ValeurCourante is { } v ? ContenuSecretCertificat.DepuisJson(v)?.ExpireLe : null, maintenant)))
            .OrderBy(m => m.Identite, StringComparer.Ordinal)
            .ToList();
        if (mesures.Count > 0)
        {
            mesures.Add(new MesureEcheance(IdentiteMinimum, mesures.Min(m => m.JoursRestants)));
        }

        mesures.Add(new MesureEcheance(IdentiteIntermediaire, JoursRestants(new DateTimeOffset(autorite.Certificat.NotAfter.ToUniversalTime()), maintenant)));

        await publicateur.PublierAsync(mesures, cancellationToken).ConfigureAwait(false);
        return mesures;
    }

    private static double JoursRestants(DateTimeOffset? echeance, DateTimeOffset maintenant) =>
        echeance is { } e && e > maintenant ? Math.Round((e - maintenant).TotalDays, 2) : 0;
}
