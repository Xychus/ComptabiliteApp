using System.Globalization;
using System.Text.RegularExpressions;
using Ledger.Query.Application.Consultations;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ledger.Query.Api.Http;

/// <summary>Erreurs de forme des paramètres d'une requête, par nom de paramètre.</summary>
public sealed class ErreursParametres
{
    private readonly Dictionary<string, List<string>> _erreurs = new(StringComparer.Ordinal);

    public bool Existe => _erreurs.Count > 0;

    public void Ajouter(string parametre, string message)
    {
        if (!_erreurs.TryGetValue(parametre, out var messages))
        {
            messages = [];
            _erreurs.Add(parametre, messages);
        }

        messages.Add(message);
    }

    public ValidationProblem VersProbleme() =>
        TypedResults.ValidationProblem(
            _erreurs.ToDictionary(e => e.Key, e => e.Value.ToArray(), StringComparer.Ordinal),
            title: "Requête invalide",
            type: ProblemesConsultation.PrefixeType + "InvalidRequest");
}

/// <summary>
/// Vérifications de forme des paramètres (mêmes formats que l'API de commandes). Une valeur
/// invalide est signalée dans <see cref="ErreursParametres"/> et la méthode renvoie <see langword="null"/>.
/// </summary>
public static partial class ParametresRequete
{
    public const int LongueurMaxEntite = 64;
    public const int LongueurMaxCompte = 32;

    public static string? Entite(string? valeur, ErreursParametres erreurs)
    {
        ArgumentNullException.ThrowIfNull(erreurs);
        if (valeur is { Length: > 0 and <= LongueurMaxEntite } && Identifiant().IsMatch(valeur))
        {
            return valeur;
        }

        erreurs.Ajouter("entityId", $"Identifiant d'entité obligatoire : 1 à {LongueurMaxEntite} caractères parmi lettres, chiffres, « . », « _ » et « - ».");
        return null;
    }

    public static string? Compte(string valeur, ErreursParametres erreurs)
    {
        ArgumentNullException.ThrowIfNull(erreurs);
        if (valeur is { Length: > 0 and <= LongueurMaxCompte } && Identifiant().IsMatch(valeur))
        {
            return valeur;
        }

        erreurs.Ajouter("accountId", $"Code de compte attendu : 1 à {LongueurMaxCompte} caractères parmi lettres, chiffres, « . », « _ » et « - ».");
        return null;
    }

    public static string? Periode(string? valeur, ErreursParametres erreurs)
    {
        ArgumentNullException.ThrowIfNull(erreurs);
        if (valeur is not null && FormatPeriode().IsMatch(valeur))
        {
            return valeur;
        }

        erreurs.Ajouter("period", "Période obligatoire au format AAAA-MM (mois 01 à 12).");
        return null;
    }

    /// <summary>Date d'affaires facultative (AAAA-MM-JJ) ; renvoie faux si elle est présente et mal formée.</summary>
    public static bool Date(string? valeur, ErreursParametres erreurs, out DateOnly? date)
    {
        ArgumentNullException.ThrowIfNull(erreurs);
        date = null;
        if (valeur is null)
        {
            return true;
        }

        if (DateOnly.TryParseExact(valeur, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var lue))
        {
            date = lue;
            return true;
        }

        erreurs.Ajouter("asOf", "Date d'affaires attendue au format AAAA-MM-JJ.");
        return false;
    }

    public static int Taille(int? valeur, ErreursParametres erreurs)
    {
        ArgumentNullException.ThrowIfNull(erreurs);
        var taille = valeur ?? ConsulterLignesCompte.TailleParDefaut;
        if (taille is >= 1 and <= ConsulterLignesCompte.TailleMaximale)
        {
            return taille;
        }

        erreurs.Ajouter("limit", $"Taille de page entre 1 et {ConsulterLignesCompte.TailleMaximale}.");
        return ConsulterLignesCompte.TailleParDefaut;
    }

    public static Guid? Ecriture(string valeur, ErreursParametres erreurs)
    {
        ArgumentNullException.ThrowIfNull(erreurs);
        if (Guid.TryParseExact(valeur, "D", out var ecriture))
        {
            return ecriture;
        }

        erreurs.Ajouter("entryId", "Identifiant d'écriture attendu : UUID au format 8-4-4-4-12.");
        return null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9._-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex Identifiant();

    [GeneratedRegex(@"^[0-9]{4}-(0[1-9]|1[0-2])\z", RegexOptions.CultureInvariant)]
    private static partial Regex FormatPeriode();
}
