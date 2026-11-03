using Ledger.Pki.Application.Emission;
using Ledger.SharedKernel;

namespace Ledger.Pki.Application.Sentinelle;

/// <summary>Certificat courant d'une identité : identifiant de version et contenu.</summary>
public sealed record CertificatCourant(string Version, ContenuSecretCertificat Contenu);

/// <summary>Port de lecture du certificat courant de l'identité surveillée (secret Secrets Manager).</summary>
public interface ISourceCertificatCourant
{
    Task<CertificatCourant> LireAsync(CancellationToken cancellationToken);

    /// <summary>Identifiant de la version courante, sans lire la valeur.</summary>
    Task<string> LireVersionAsync(CancellationToken cancellationToken);
}

/// <summary>Port d'écriture des fichiers PEM lus par le broker (keystore et truststore de type PEM).</summary>
public interface IEcrivainCertificats
{
    Task EcrireAsync(CertificatCourant certificat, CancellationToken cancellationToken);

    /// <summary>Certificat installé au démarrage de la tâche ; <see langword="null"/> si aucun.</summary>
    Task<CertificatInstalle?> LireInstalleAsync(CancellationToken cancellationToken);
}

/// <summary>Version et échéance du certificat installé.</summary>
public sealed record CertificatInstalle(string Version, DateTimeOffset ExpireLe);

/// <summary>État de la grappe Kafka vu par le nœud surveillé.</summary>
public sealed record EtatGrappe(bool Saine, string Detail);

/// <summary>Port d'évaluation de la santé de la grappe (tous les nœuds présents, aucune partition sous-répliquée).</summary>
public interface ISanteGrappe
{
    Task<EtatGrappe> EvaluerAsync(ContenuSecretCertificat identite, CancellationToken cancellationToken);
}

/// <summary>Réglages de la sentinelle (section <c>Ledger:Sentinelle</c>).</summary>
public sealed class OptionsSentinelle
{
    public const string Section = "Ledger:Sentinelle";

    /// <summary>Créneau du nœud (0 à <see cref="NombreCreneaux"/> − 1) : deux nœuds ne redémarrent jamais dans le même créneau.</summary>
    public int Creneau { get; set; }

    public int NombreCreneaux { get; set; } = 3;

    public TimeSpan DureeCreneau { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan Intervalle { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>En deçà de cette validité restante, le créneau est ignoré (la santé reste exigée).</summary>
    public TimeSpan SeuilUrgence { get; set; } = TimeSpan.FromHours(12);
}

public enum DecisionSentinelle
{
    Continuer,
    Redemarrer,
}

/// <summary>Décision et sa raison (journalisée).</summary>
public sealed record VerdictSentinelle(DecisionSentinelle Decision, string Raison);

/// <summary>
/// Sentinelle d'un broker Kafka (ADR 0024, révisée) : installe le certificat au démarrage de la
/// tâche, puis surveille la version du secret. Une nouvelle version déclenche l'arrêt de la tâche
/// (ECS la relance avec le nouveau certificat), seulement dans le créneau du nœud et si la grappe
/// est saine : un seul nœud à la fois, jamais sous la réplication minimale. Le rechargement à
/// chaud n'est pas garanti pour l'écouteur contrôleur de KRaft.
/// </summary>
public sealed class Sentinelle(ISourceCertificatCourant source, IEcrivainCertificats ecrivain, ISanteGrappe sante, IClock horloge, OptionsSentinelle options)
{
    public async Task<string> InstallerAsync(CancellationToken cancellationToken)
    {
        var courant = await source.LireAsync(cancellationToken).ConfigureAwait(false);
        await ecrivain.EcrireAsync(courant, cancellationToken).ConfigureAwait(false);
        return courant.Version;
    }

    public async Task<VerdictSentinelle> VerifierAsync(CancellationToken cancellationToken)
    {
        var installe = await ecrivain.LireInstalleAsync(cancellationToken).ConfigureAwait(false);
        var courante = await source.LireVersionAsync(cancellationToken).ConfigureAwait(false);
        if (installe?.Version == courante)
        {
            return Continuer("Certificat à jour.");
        }

        var nouveau = await source.LireAsync(cancellationToken).ConfigureAwait(false);
        var maintenant = horloge.UtcNow;
        var validiteInstallee = installe is null ? TimeSpan.Zero : installe.ExpireLe - maintenant;
        var urgence = nouveau.Contenu.ExpireLe > maintenant && validiteInstallee < options.SeuilUrgence;
        if (!urgence && !DansLeCreneau(maintenant))
        {
            return Continuer($"Nouveau certificat {courante} : attente du créneau {options.Creneau}.");
        }

        var etat = await sante.EvaluerAsync(nouveau.Contenu, cancellationToken).ConfigureAwait(false);
        if (!etat.Saine)
        {
            return Continuer($"Nouveau certificat {courante} : grappe non saine ({etat.Detail}), redémarrage différé.");
        }

        return new VerdictSentinelle(DecisionSentinelle.Redemarrer, $"Nouveau certificat {courante}{(urgence ? " (urgence)" : string.Empty)} : redémarrage du nœud.");
    }

    private bool DansLeCreneau(DateTimeOffset maintenant)
    {
        var index = (long)Math.Floor(maintenant.ToUnixTimeSeconds() / options.DureeCreneau.TotalSeconds);
        return index % options.NombreCreneaux == options.Creneau;
    }

    private static VerdictSentinelle Continuer(string raison) => new(DecisionSentinelle.Continuer, raison);
}
