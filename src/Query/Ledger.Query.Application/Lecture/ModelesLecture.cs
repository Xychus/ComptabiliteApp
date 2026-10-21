namespace Ledger.Query.Application.Lecture;

// Lignes lues dans les projections (schéma projections, migration 0007). Montants en decimal,
// toujours accompagnés de leur devise ; sens « Debit » / « Credit » tels qu'enregistrés.

/// <summary>Compte du plan comptable d'une entité (projection <c>chart_of_accounts</c>).</summary>
public sealed record CompteLu(string Entite, string Compte, string Nom, string Devise, string SensNormal, bool EstActif);

/// <summary>Débits et crédits cumulés d'un compte dans une devise.</summary>
public sealed record CumulCompte(string Compte, string Devise, decimal Debits, decimal Credits);

/// <summary>
/// Position d'une ligne dans le sous-livre d'un compte : ordre comptable (date d'affaires, puis
/// séquence du journal, puis numéro de ligne). Jamais d'ordre par UUID.
/// </summary>
public sealed record PositionLigne(DateOnly DateAffaires, long Sequence, int NumeroLigne);

/// <summary>Ligne du sous-livre d'un compte, avec le libellé de son écriture.</summary>
public sealed record LigneCompte(
    Guid Ecriture,
    int NumeroLigne,
    DateOnly DateAffaires,
    string Periode,
    string Sens,
    decimal Montant,
    string Devise,
    long Sequence,
    string? Libelle)
{
    public PositionLigne Position => new(DateAffaires, Sequence, NumeroLigne);
}

/// <summary>Ligne d'une écriture.</summary>
public sealed record LigneEcritureLue(int NumeroLigne, string Compte, string Sens, decimal Montant, string Devise);

/// <summary>Écriture du grand livre (projection <c>ledger_entries</c>).</summary>
public sealed record EcritureLue(
    Guid Ecriture,
    string Entite,
    DateOnly DateAffaires,
    string Periode,
    string? Libelle,
    Guid? ContrePassationDe,
    Guid? ContrePasseePar,
    long Sequence,
    DateTimeOffset EnregistreeLe,
    string Acteur,
    IReadOnlyList<LigneEcritureLue> Lignes);

/// <summary>Statut d'une période et empreinte de son sceau (projection <c>periods</c>).</summary>
public sealed record PeriodeLue(
    string Entite,
    string Periode,
    string Statut,
    long Version,
    DateTimeOffset? OuverteLe,
    DateTimeOffset? ScelleeLe,
    long? NombreEvenements,
    string? RacineMerkle);

/// <summary>Mouvements d'une période scellée, par compte et devise (projection <c>period_snapshots</c>, ADR 0017).</summary>
public sealed record InstantanePeriode(long SequenceSceau, long NombreEvenements, IReadOnlyList<CumulCompte> Mouvements);

/// <summary>TPS et TVQ perçues et payées d'une période, dans une devise (projection <c>tax_projection</c>, ADR 0018).</summary>
public sealed record TaxesPeriode(string Devise, decimal TpsPercue, decimal TpsPayee, decimal TvqPercue, decimal TvqPayee);

/// <summary>
/// Repères de fraîcheur : position la plus basse des projections consultées, tête du journal, et
/// horodatage du plus ancien événement que ces projections n'ont pas encore vu.
/// </summary>
/// <param name="JusquaSequence">Plus petite position des projections (0 si l'une n'a encore rien vu).</param>
/// <param name="SequenceJournal">Plus grande séquence du journal (0 s'il est vide).</param>
/// <param name="PlusAncienEnAttente">Horodatage du premier événement après <paramref name="JusquaSequence"/>, ou rien si la projection est à jour.</param>
public sealed record ReperesFraicheur(long JusquaSequence, long SequenceJournal, DateTimeOffset? PlusAncienEnAttente);
