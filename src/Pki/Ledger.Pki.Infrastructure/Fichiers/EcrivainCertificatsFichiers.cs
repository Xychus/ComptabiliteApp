using System.Globalization;
using Ledger.Pki.Application.Sentinelle;

namespace Ledger.Pki.Infrastructure.Fichiers;

/// <summary>
/// Écrit les fichiers lus par le broker dans un volume partagé de la tâche : <c>keystore.pem</c>
/// (clé PKCS#8 puis chaîne, keystore de type PEM de Kafka) et <c>truststore.pem</c> (racine).
/// Écriture atomique (fichier temporaire puis renommage), lecture réservée au propriétaire.
/// </summary>
public sealed class EcrivainCertificatsFichiers(string dossier) : IEcrivainCertificats
{
    public const string Keystore = "keystore.pem";
    public const string Truststore = "truststore.pem";
    public const string Version = "version";

    public async Task EcrireAsync(CertificatCourant certificat, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(certificat);

        Directory.CreateDirectory(dossier);
        await EcrireAtomiquementAsync(Keystore, $"{certificat.Contenu.Cle.TrimEnd()}\n{certificat.Contenu.Chaine.TrimEnd()}\n", cancellationToken).ConfigureAwait(false);
        await EcrireAtomiquementAsync(Truststore, $"{certificat.Contenu.Racine.TrimEnd()}\n", cancellationToken).ConfigureAwait(false);
        await EcrireAtomiquementAsync(Version, $"{certificat.Version}\n{certificat.Contenu.ExpireLe.ToString("O", CultureInfo.InvariantCulture)}\n", cancellationToken).ConfigureAwait(false);
    }

    public async Task<CertificatInstalle?> LireInstalleAsync(CancellationToken cancellationToken)
    {
        var chemin = Path.Join(dossier, Version);
        if (!File.Exists(chemin))
        {
            return null;
        }

        var lignes = await File.ReadAllLinesAsync(chemin, cancellationToken).ConfigureAwait(false);
        return lignes.Length >= 2 && DateTimeOffset.TryParse(lignes[1], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expireLe)
            ? new CertificatInstalle(lignes[0], expireLe)
            : null;
    }

    private async Task EcrireAtomiquementAsync(string nom, string contenu, CancellationToken cancellationToken)
    {
        var cible = Path.Join(dossier, nom);
        var temporaire = $"{cible}.{Guid.NewGuid():N}.tmp";
        await File.WriteAllTextAsync(temporaire, contenu, cancellationToken).ConfigureAwait(false);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporaire, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporaire, cible, overwrite: true);
    }
}
