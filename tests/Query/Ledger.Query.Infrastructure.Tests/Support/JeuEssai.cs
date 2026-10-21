using Ledger.Query.Application.Lecture;
using Ledger.Query.Infrastructure.Memoire;

namespace Ledger.Query.Tests.Support;

/// <summary>
/// Lignes de projection d'une entité, comme les aurait écrites le worker : chaque écriture ajoute
/// ses lignes au grand livre et cumule les soldes courants. Partagé par les tests d'Application,
/// de contrat (mémoire et PostgreSQL) et d'API.
/// </summary>
public sealed class JeuEssai(string entite)
{
    public const string Devise = "CAD";
    public const string Acteur = "client-tests";
    public const string Mars = "2026-03";
    public const string Avril = "2026-04";

    public static readonly DateTimeOffset Origine = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly List<CompteLu> _comptes = [];
    private readonly List<EcritureEnregistree> _ecritures = [];
    private readonly List<LigneEnregistree> _lignes = [];
    private readonly List<PeriodeLue> _periodes = [];
    private readonly List<InstantaneEnregistre> _instantanes = [];
    private readonly List<TaxesEnregistrees> _taxes = [];

    public string Entite { get; } = entite;

    public IReadOnlyList<CompteLu> Comptes => _comptes;

    public IReadOnlyList<EcritureEnregistree> Ecritures => _ecritures;

    public IReadOnlyList<LigneEnregistree> Lignes => _lignes;

    public IReadOnlyList<PeriodeLue> Periodes => _periodes;

    public IReadOnlyList<InstantaneEnregistre> Instantanes => _instantanes;

    public IReadOnlyList<TaxesEnregistrees> Taxes => _taxes;

    /// <summary>Soldes courants : cumul des lignes par compte et devise (projection <c>account_balances</c>).</summary>
    public IReadOnlyList<SoldeEnregistre> Soldes =>
        [
            .. _lignes.GroupBy(l => (l.Compte, l.Devise)).Select(g => new SoldeEnregistre(
                Entite,
                g.Key.Compte,
                g.Key.Devise,
                g.Where(l => l.Sens == "Debit").Sum(l => l.Montant),
                g.Where(l => l.Sens == "Credit").Sum(l => l.Montant))),
        ];

    public static (string Compte, string Sens, decimal Montant, string Devise) Debit(string compte, decimal montant, string devise = Devise) => (compte, "Debit", montant, devise);

    public static (string Compte, string Sens, decimal Montant, string Devise) Credit(string compte, decimal montant, string devise = Devise) => (compte, "Credit", montant, devise);

    public JeuEssai Compte(string compte, string nom, string sensNormal = "Debit", string devise = Devise, bool actif = true)
    {
        _comptes.Add(new CompteLu(Entite, compte, nom, devise, sensNormal, actif));
        return this;
    }

    /// <summary>Écriture et ses lignes, numérotées à partir de 1 dans l'ordre donné.</summary>
    public JeuEssai Ecriture(
        Guid ecriture,
        DateOnly date,
        long sequence,
        IReadOnlyList<(string Compte, string Sens, decimal Montant, string Devise)> lignes,
        string? libelle = null,
        Guid? contrePassationDe = null,
        Guid? contrePasseePar = null)
    {
        ArgumentNullException.ThrowIfNull(lignes);
        var periode = date.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        _ecritures.Add(new EcritureEnregistree(ecriture, Entite, date, periode, libelle, contrePassationDe, contrePasseePar, sequence, Origine.AddSeconds(sequence), Acteur));
        for (var index = 0; index < lignes.Count; index++)
        {
            var (compte, sens, montant, devise) = lignes[index];
            _lignes.Add(new LigneEnregistree(ecriture, index + 1, Entite, compte, sens, montant, devise, date, periode, sequence));
        }

        return this;
    }

    public JeuEssai Periode(string periode, string statut = "Open", long version = 1, DateTimeOffset? scelleeLe = null, long? nombreEvenements = null, string? racineMerkle = null)
    {
        _periodes.Add(new PeriodeLue(Entite, periode, statut, version, Origine, scelleeLe, nombreEvenements, racineMerkle));
        return this;
    }

    public JeuEssai Instantane(string periode, long sequenceSceau, long nombreEvenements, params CumulCompte[] mouvements)
    {
        _instantanes.AddRange(mouvements.Select(m => new InstantaneEnregistre(Entite, periode, m.Compte, m.Devise, m.Debits, m.Credits, sequenceSceau, nombreEvenements)));
        return this;
    }

    public JeuEssai TaxesPeriode(string periode, string devise, decimal tpsPercue, decimal tpsPayee, decimal tvqPercue, decimal tvqPayee)
    {
        _taxes.Add(new TaxesEnregistrees(Entite, periode, devise, tpsPercue, tpsPayee, tvqPercue, tvqPayee));
        return this;
    }

    /// <summary>Ajoute ces lignes aux tables en mémoire.</summary>
    public TablesLecture AppliquerA(TablesLecture tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        return tables with
        {
            Comptes = tables.Comptes.AddRange(_comptes),
            Soldes = tables.Soldes.AddRange(Soldes),
            Ecritures = tables.Ecritures.AddRange(_ecritures),
            Lignes = tables.Lignes.AddRange(_lignes),
            Periodes = tables.Periodes.AddRange(_periodes),
            Instantanes = tables.Instantanes.AddRange(_instantanes),
            Taxes = tables.Taxes.AddRange(_taxes),
        };
    }
}

/// <summary>Position des projections et journal en mémoire (fraîcheur).</summary>
public static class ReperesEnMemoire
{
    public static TablesLecture AvecPosition(this TablesLecture tables, string projection, long sequence)
    {
        ArgumentNullException.ThrowIfNull(tables);
        return tables with { Positions = tables.Positions.SetItem(projection, sequence) };
    }

    public static TablesLecture AvecEvenement(this TablesLecture tables, long sequence, DateTimeOffset enregistreLe)
    {
        ArgumentNullException.ThrowIfNull(tables);
        return tables with { Journal = tables.Journal.Add(new EvenementEnregistre(sequence, enregistreLe)) };
    }

    /// <summary>Toutes les projections à jour jusqu'à <paramref name="sequence"/>.</summary>
    public static TablesLecture AJourJusqua(this TablesLecture tables, long sequence)
    {
        ArgumentNullException.ThrowIfNull(tables);
        string[] toutes = [NomsProjections.Soldes, NomsProjections.GrandLivre, NomsProjections.PlanComptable, NomsProjections.Periodes, NomsProjections.Instantanes, NomsProjections.Taxes];
        return tables with { Positions = tables.Positions.SetItems(toutes.Select(p => KeyValuePair.Create(p, sequence))) };
    }
}
