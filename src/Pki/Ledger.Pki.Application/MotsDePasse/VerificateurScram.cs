using System.Security.Cryptography;
using System.Text;

namespace Ledger.Pki.Application.MotsDePasse;

/// <summary>
/// Vérificateur SCRAM-SHA-256 au format de PostgreSQL (RFC 5802, 7677) :
/// <c>SCRAM-SHA-256$&lt;itérations&gt;:&lt;sel&gt;$&lt;StoredKey&gt;:&lt;ServerKey&gt;</c>. Transmis à
/// <c>ALTER ROLE … PASSWORD</c> à la place du mot de passe : la journalisation des DDL d'Aurora
/// n'écrit jamais le mot de passe en clair.
/// </summary>
public static class VerificateurScram
{
    public const int Iterations = 4096;

    public static string Calculer(string motDePasse, byte[]? sel = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(motDePasse);

        sel ??= RandomNumberGenerator.GetBytes(16);
        var motDePasseSale = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(motDePasse), sel, Iterations, HashAlgorithmName.SHA256, 32);
        var cleClient = HMACSHA256.HashData(motDePasseSale, "Client Key"u8);
        var cleStockee = SHA256.HashData(cleClient);
        var cleServeur = HMACSHA256.HashData(motDePasseSale, "Server Key"u8);
        return $"SCRAM-SHA-256${Iterations}:{Convert.ToBase64String(sel)}${Convert.ToBase64String(cleStockee)}:{Convert.ToBase64String(cleServeur)}";
    }
}
