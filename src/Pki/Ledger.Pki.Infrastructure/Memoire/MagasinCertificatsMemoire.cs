using Ledger.Pki.Application.Inventaire;
using Ledger.Pki.Application.Profils;
using Ledger.Pki.Application.Rotation;

namespace Ledger.Pki.Infrastructure.Memoire;

/// <summary>
/// Magasin en mémoire, fidèle au protocole de Secrets Manager (versions, étiquettes de version
/// exclusives, écriture idempotente par jeton). Pour les tests et le développement.
/// </summary>
public sealed class MagasinCertificatsMemoire : IMagasinCertificats, ISourceInventaire
{
    private readonly Lock _verrou = new();
    private readonly Dictionary<string, SecretMemoire> _secrets = new(StringComparer.Ordinal);

    /// <summary>Crée un secret vide (sans version), comme Terraform le fait avant la première rotation.</summary>
    public void Creer(string secret, IReadOnlyDictionary<string, string> etiquettes, bool rotationActivee = true)
    {
        lock (_verrou)
        {
            _secrets[secret] = new SecretMemoire(new Dictionary<string, string>(etiquettes, StringComparer.Ordinal), rotationActivee);
        }
    }

    /// <summary>Ajoute une version portant l'étiquette <see cref="EtiquettesVersion.EnAttente"/>, comme Secrets Manager au lancement d'une rotation.</summary>
    public void AmorcerRotation(string secret, string version)
    {
        lock (_verrou)
        {
            var s = Secret(secret);
            Retirer(s, EtiquettesVersion.EnAttente);
            s.Versions[version] = new VersionMemoire(null, [EtiquettesVersion.EnAttente]);
        }
    }

    public Task<DescriptionSecret> DecrireAsync(string secret, CancellationToken cancellationToken)
    {
        lock (_verrou)
        {
            var s = Secret(secret);
            var versions = s.Versions.ToDictionary(v => v.Key, v => (IReadOnlyList<string>)[.. v.Value.Etiquettes], StringComparer.Ordinal);
            return Task.FromResult(new DescriptionSecret(secret, s.RotationActivee, versions, new Dictionary<string, string>(s.Etiquettes, StringComparer.Ordinal)));
        }
    }

    public Task<string?> LireParEtiquetteAsync(string secret, string etiquette, CancellationToken cancellationToken)
    {
        lock (_verrou)
        {
            var version = Secret(secret).Versions.Values.FirstOrDefault(v => v.Etiquettes.Contains(etiquette));
            return Task.FromResult(version?.Valeur);
        }
    }

    public Task<string?> LireVersionAsync(string secret, string version, CancellationToken cancellationToken)
    {
        lock (_verrou)
        {
            return Task.FromResult(Secret(secret).Versions.GetValueOrDefault(version)?.Valeur);
        }
    }

    public Task EcrireEnAttenteAsync(string secret, string version, string valeur, CancellationToken cancellationToken)
    {
        lock (_verrou)
        {
            var s = Secret(secret);
            if (s.Versions.TryGetValue(version, out var existante) && existante.Valeur is not null)
            {
                // Secrets Manager : même jeton et même valeur = idempotent ; autre valeur = refus.
                return existante.Valeur == valeur
                    ? Task.CompletedTask
                    : throw new InvalidOperationException($"La version {version} existe déjà avec une autre valeur.");
            }

            Retirer(s, EtiquettesVersion.EnAttente);
            s.Versions[version] = new VersionMemoire(valeur, [EtiquettesVersion.EnAttente]);
            return Task.CompletedTask;
        }
    }

    public Task PromouvoirAsync(string secret, string version, string? versionCourante, CancellationToken cancellationToken)
    {
        lock (_verrou)
        {
            var s = Secret(secret);
            if (!s.Versions.TryGetValue(version, out var cible))
            {
                throw new InvalidOperationException($"Version inconnue : {version}.");
            }

            var courante = s.Versions.FirstOrDefault(v => v.Value.Etiquettes.Contains(EtiquettesVersion.Courante)).Key;
            if (courante is not null && courante != versionCourante)
            {
                throw new InvalidOperationException($"La version courante est {courante}, pas {versionCourante}.");
            }

            if (courante is not null)
            {
                s.Versions[courante].Etiquettes.Remove(EtiquettesVersion.Courante);
                s.Versions[courante].Etiquettes.Add("AWSPREVIOUS");
            }

            cible.Etiquettes.Remove(EtiquettesVersion.EnAttente);
            cible.Etiquettes.Add(EtiquettesVersion.Courante);
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<SecretRecense>> ListerAsync(CancellationToken cancellationToken)
    {
        lock (_verrou)
        {
            IReadOnlyList<SecretRecense> recenses = [.. _secrets
                .Where(s => s.Value.Etiquettes.ContainsKey(ProfilCertificat.EtiquetteNomCommun))
                .Select(s => new SecretRecense(
                    s.Value.Etiquettes[ProfilCertificat.EtiquetteNomCommun],
                    s.Value.Versions.Values.FirstOrDefault(v => v.Etiquettes.Contains(EtiquettesVersion.Courante))?.Valeur))];
            return Task.FromResult(recenses);
        }
    }

    private SecretMemoire Secret(string secret) =>
        _secrets.TryGetValue(secret, out var s) ? s : throw new KeyNotFoundException($"Secret inconnu : {secret}.");

    private static void Retirer(SecretMemoire secret, string etiquette)
    {
        foreach (var version in secret.Versions.Values)
        {
            version.Etiquettes.Remove(etiquette);
        }
    }

    private sealed record SecretMemoire(Dictionary<string, string> Etiquettes, bool RotationActivee)
    {
        public Dictionary<string, VersionMemoire> Versions { get; } = new(StringComparer.Ordinal);
    }

    private sealed record VersionMemoire(string? Valeur, HashSet<string> Etiquettes);
}
