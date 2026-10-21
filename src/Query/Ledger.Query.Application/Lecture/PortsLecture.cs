namespace Ledger.Query.Application.Lecture;

// Ports de lecture des projections : un par besoin de consultation (interfaces étroites).
// Aucun n'écrit ; les implémentations lisent les tables en service du schéma projections.

/// <summary>Noms des projections lues, identiques à ceux du worker (positions de fraîcheur).</summary>
public static class NomsProjections
{
    public const string Soldes = "account_balances";
    public const string GrandLivre = "ledger_entries";
    public const string PlanComptable = "chart_of_accounts";
    public const string Periodes = "periods";
    public const string Instantanes = "period_snapshots";
    public const string Taxes = "tax_projection";
}

public interface ILecteurPlanComptable
{
    Task<CompteLu?> TrouverCompteAsync(string entite, string compte, CancellationToken cancellationToken);

    Task<IReadOnlyList<CompteLu>> LireComptesAsync(string entite, CancellationToken cancellationToken);
}

/// <summary>Soldes courants (<c>account_balances</c>).</summary>
public interface ILecteurSoldes
{
    /// <summary>Cumuls d'un compte, ou de tous les comptes de l'entité si <paramref name="compte"/> est nul.</summary>
    Task<IReadOnlyList<CumulCompte>> LireSoldesAsync(string entite, string? compte, CancellationToken cancellationToken);
}

/// <summary>Cumuls des lignes du grand livre jusqu'à une date d'affaires (<c>ledger_lines</c>).</summary>
public interface ILecteurMouvements
{
    /// <summary>Cumuls des lignes dont la date d'affaires est au plus <paramref name="auPlusTard"/> (incluse).</summary>
    Task<IReadOnlyList<CumulCompte>> CumulerAsync(string entite, string? compte, DateOnly auPlusTard, CancellationToken cancellationToken);
}

/// <summary>Sous-livre d'un compte, par pages (<c>ledger_lines</c> et <c>journal_entries</c>).</summary>
public interface ILecteurLignesCompte
{
    /// <summary>Au plus <paramref name="nombre"/> lignes strictement après <paramref name="apres"/>, dans l'ordre comptable.</summary>
    Task<IReadOnlyList<LigneCompte>> LireLignesAsync(string entite, string compte, PositionLigne? apres, int nombre, CancellationToken cancellationToken);
}

public interface ILecteurEcritures
{
    Task<EcritureLue?> TrouverEcritureAsync(Guid ecriture, CancellationToken cancellationToken);
}

public interface ILecteurPeriodes
{
    Task<PeriodeLue?> TrouverPeriodeAsync(string entite, string periode, CancellationToken cancellationToken);
}

public interface ILecteurInstantanes
{
    /// <summary>Instantané d'une période scellée ; rien si elle n'est pas scellée ou n'a aucune écriture (ADR 0017).</summary>
    Task<InstantanePeriode?> TrouverInstantaneAsync(string entite, string periode, CancellationToken cancellationToken);
}

public interface ILecteurTaxes
{
    /// <summary>Une ligne par devise, triées par devise.</summary>
    Task<IReadOnlyList<TaxesPeriode>> LireTaxesAsync(string entite, string periode, CancellationToken cancellationToken);
}

/// <summary>Positions des projections et tête du journal (<c>projection_positions</c>, <c>ledger.events</c>).</summary>
public interface ILecteurReperesFraicheur
{
    Task<ReperesFraicheur> LireAsync(IReadOnlyCollection<string> projections, CancellationToken cancellationToken);
}
