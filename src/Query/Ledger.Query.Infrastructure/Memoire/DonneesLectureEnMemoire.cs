using System.Collections.Immutable;
using Ledger.Query.Application.Lecture;

namespace Ledger.Query.Infrastructure.Memoire;

// Lignes des tables en mémoire : mêmes colonnes que le schéma projections (migration 0007) et que
// les colonnes de ledger.events utiles à la fraîcheur. Le plan comptable et les périodes
// reprennent directement les modèles de lecture, qui ont exactement leurs colonnes.

public sealed record SoldeEnregistre(string Entite, string Compte, string Devise, decimal Debits, decimal Credits);

public sealed record EcritureEnregistree(
    Guid Ecriture,
    string Entite,
    DateOnly DateAffaires,
    string Periode,
    string? Libelle,
    Guid? ContrePassationDe,
    Guid? ContrePasseePar,
    long Sequence,
    DateTimeOffset EnregistreeLe,
    string Acteur);

public sealed record LigneEnregistree(Guid Ecriture, int NumeroLigne, string Entite, string Compte, string Sens, decimal Montant, string Devise, DateOnly DateAffaires, string Periode, long Sequence);

public sealed record InstantaneEnregistre(string Entite, string Periode, string Compte, string Devise, decimal Debits, decimal Credits, long SequenceSceau, long NombreEvenements);

public sealed record TaxesEnregistrees(string Entite, string Periode, string Devise, decimal TpsPercue, decimal TpsPayee, decimal TvqPercue, decimal TvqPayee);

public sealed record PositionEnregistree(string Projection, long JusquaSequence);

public sealed record EvenementEnregistre(long Sequence, DateTimeOffset EnregistreLe);

/// <summary>Contenu des tables : un instantané immuable, remplacé d'un bloc à chaque modification.</summary>
public sealed record TablesLecture
{
    public ImmutableList<CompteLu> Comptes { get; init; } = [];

    public ImmutableList<SoldeEnregistre> Soldes { get; init; } = [];

    public ImmutableList<EcritureEnregistree> Ecritures { get; init; } = [];

    public ImmutableList<LigneEnregistree> Lignes { get; init; } = [];

    public ImmutableList<PeriodeLue> Periodes { get; init; } = [];

    public ImmutableList<InstantaneEnregistre> Instantanes { get; init; } = [];

    public ImmutableList<TaxesEnregistrees> Taxes { get; init; } = [];

    /// <summary>Position par projection (une seule ligne par projection).</summary>
    public ImmutableDictionary<string, long> Positions { get; init; } = ImmutableDictionary.Create<string, long>(StringComparer.Ordinal);

    public ImmutableList<EvenementEnregistre> Journal { get; init; } = [];
}

/// <summary>
/// Modèles de lecture en mémoire, pour les tests et le développement : alimentés directement
/// (aucun worker en mémoire), lus par les mêmes ports que PostgreSQL.
/// </summary>
public sealed class DonneesLectureEnMemoire
{
    private readonly Lock _verrou = new();
    private TablesLecture _tables = new();

    public TablesLecture Tables => Volatile.Read(ref _tables);

    public void Modifier(Func<TablesLecture, TablesLecture> modification)
    {
        ArgumentNullException.ThrowIfNull(modification);
        lock (_verrou)
        {
            Volatile.Write(ref _tables, modification(_tables));
        }
    }
}
