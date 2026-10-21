using System.Buffers.Text;
using System.Globalization;
using System.Text;
using Ledger.Query.Application.Lecture;

namespace Ledger.Query.Application.Consultations;

/// <summary>
/// Curseur opaque de pagination du sous-livre : la position de la dernière ligne renvoyée
/// (date d'affaires, séquence, numéro de ligne), en base64url. Stable malgré les insertions :
/// une ligne ajoutée avant le curseur n'en décale aucune autre.
/// </summary>
public static class CurseurLignes
{
    private const string Version = "1";
    private const string Separateur = "|";

    public static string Encoder(PositionLigne position)
    {
        ArgumentNullException.ThrowIfNull(position);
        var texte = string.Join(
            Separateur,
            Version,
            position.DateAffaires.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            position.Sequence.ToString(CultureInfo.InvariantCulture),
            position.NumeroLigne.ToString(CultureInfo.InvariantCulture));
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(texte));
    }

    public static bool TryDecoder(string curseur, out PositionLigne? position)
    {
        ArgumentNullException.ThrowIfNull(curseur);
        position = null;
        byte[] octets;
        try
        {
            octets = Base64Url.DecodeFromChars(curseur);
        }
        catch (FormatException)
        {
            return false;
        }

        var parties = Encoding.UTF8.GetString(octets).Split(Separateur);
        if (parties.Length != 4
            || parties[0] != Version
            || !DateOnly.TryParseExact(parties[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            || !long.TryParse(parties[2], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            || !int.TryParse(parties[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ligne))
        {
            return false;
        }

        position = new PositionLigne(date, sequence, ligne);
        return true;
    }
}
