using Ledger.Query.Application.Lecture;
using Ledger.SharedKernel;

namespace Ledger.Query.Application.Fraicheur;

/// <summary>Seuil de l'indicateur de fraîcheur (section <c>Ledger:Fraicheur</c>).</summary>
public sealed class OptionsFraicheur
{
    public const string Section = "Ledger:Fraicheur";

    /// <summary>Âge maximal du plus ancien événement pas encore projeté avant qu'une réponse soit déclarée périmée.</summary>
    public TimeSpan RetardMaximal { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>Fraîcheur d'une réponse.</summary>
/// <param name="JusquaSequence">Séquence du journal jusqu'à laquelle les projections consultées ont vu les événements.</param>
/// <param name="SequenceJournal">Tête du journal au moment de la lecture.</param>
/// <param name="Perimee">Vrai si un événement attend d'être projeté depuis plus que le seuil.</param>
public sealed record EtatFraicheur(long JusquaSequence, long SequenceJournal, bool Perimee);

/// <summary>
/// Indicateur de fraîcheur (ADR 0019) : une réponse est périmée si le plus ancien événement du
/// journal que les projections consultées n'ont pas encore vu a été enregistré il y a plus que
/// <see cref="OptionsFraicheur.RetardMaximal"/>. Un journal calme n'est jamais périmé, quelle que
/// soit l'ancienneté du dernier événement.
/// </summary>
public sealed class EvaluateurFraicheur(ILecteurReperesFraicheur reperes, IClock horloge, OptionsFraicheur options)
{
    public async Task<EtatFraicheur> EvaluerAsync(IReadOnlyCollection<string> projections, CancellationToken cancellationToken)
    {
        var lus = await reperes.LireAsync(projections, cancellationToken).ConfigureAwait(false);
        var perimee = lus.PlusAncienEnAttente is { } enAttente && horloge.UtcNow - enAttente > options.RetardMaximal;
        return new EtatFraicheur(lus.JusquaSequence, lus.SequenceJournal, perimee);
    }
}
