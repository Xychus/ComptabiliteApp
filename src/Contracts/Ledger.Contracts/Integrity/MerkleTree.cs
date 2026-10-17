using System.Security.Cryptography;

namespace Ledger.Contracts.Integrity;

/// <summary>
/// Racine de Merkle des hash d'événements d'une période (ADR 0005 et 0013) : feuilles = hash
/// des événements dans l'ordre de <c>global_sequence</c> ; nœud = SHA-256(gauche ‖ droite) ;
/// un nœud impair en fin de niveau est promu tel quel ; liste vide → SHA-256 de la chaîne vide.
/// </summary>
public static class MerkleTree
{
    /// <summary>Racine hexadécimale minuscule (64 caractères) des hash hexadécimaux fournis.</summary>
    public static string Root(IReadOnlyList<string> leafHashes)
    {
        ArgumentNullException.ThrowIfNull(leafHashes);
        if (leafHashes.Count == 0)
        {
            return Convert.ToHexStringLower(SHA256.HashData([]));
        }

        var level = leafHashes.Select(Leaf).ToList();
        while (level.Count > 1)
        {
            var parents = new List<byte[]>((level.Count + 1) / 2);
            for (var index = 0; index < level.Count; index += 2)
            {
                parents.Add(index + 1 < level.Count ? SHA256.HashData([.. level[index], .. level[index + 1]]) : level[index]);
            }

            level = parents;
        }

        return Convert.ToHexStringLower(level[0]);
    }

    private static byte[] Leaf(string hash)
    {
        if (hash is not { Length: SHA256.HashSizeInBytes * 2 } || !hash.All(char.IsAsciiHexDigitLower))
        {
            throw new ArgumentException($"Hash d'événement invalide : « {hash} » (64 caractères hexadécimaux minuscules attendus).", nameof(hash));
        }

        return Convert.FromHexString(hash);
    }
}
