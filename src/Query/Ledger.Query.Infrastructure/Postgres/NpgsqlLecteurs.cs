using Ledger.Query.Application.Lecture;
using Npgsql;

namespace Ledger.Query.Infrastructure.Postgres;

// Ports de lecture sur PostgreSQL, rôle ledger_query (SELECT seulement sur le schéma projections
// et sur ledger.events, migration 0007). Les tables lues sont toujours les tables en service :
// une reconstruction les remplace par renommage et leur redonne SELECT (ADR 0016).

/// <summary>Exécution d'une requête de lecture et projection de chaque ligne.</summary>
internal static class Requete
{
    public static async Task<IReadOnlyList<T>> LireAsync<T>(
        NpgsqlDataSource source,
        string sql,
        Action<NpgsqlParameterCollection> parametres,
        Func<NpgsqlDataReader, T> ligne,
        CancellationToken cancellationToken)
    {
        var commande = source.CreateCommand(sql);
        await using (commande.ConfigureAwait(false))
        {
            parametres(commande.Parameters);
            var lecteur = await commande.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (lecteur.ConfigureAwait(false))
            {
                var lignes = new List<T>();
                while (await lecteur.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    lignes.Add(ligne(lecteur));
                }

                return lignes;
            }
        }
    }

    public static string? TexteOuNul(this NpgsqlDataReader lecteur, int colonne) => lecteur.IsDBNull(colonne) ? null : lecteur.GetString(colonne);

    public static Guid? GuidOuNul(this NpgsqlDataReader lecteur, int colonne) => lecteur.IsDBNull(colonne) ? null : lecteur.GetGuid(colonne);

    public static long? EntierOuNul(this NpgsqlDataReader lecteur, int colonne) => lecteur.IsDBNull(colonne) ? null : lecteur.GetInt64(colonne);

    public static DateTimeOffset? InstantOuNul(this NpgsqlDataReader lecteur, int colonne) =>
        lecteur.IsDBNull(colonne) ? null : lecteur.GetFieldValue<DateTimeOffset>(colonne);
}

public sealed class NpgsqlLecteurPlanComptable(NpgsqlDataSource source) : ILecteurPlanComptable
{
    private const string Colonnes = "entity_id, account_id, name, currency, normal_side, is_active";

    public async Task<CompteLu?> TrouverCompteAsync(string entite, string compte, CancellationToken cancellationToken) =>
        (await Requete.LireAsync(
            source,
            $"SELECT {Colonnes} FROM projections.chart_of_accounts WHERE entity_id = @entite AND account_id = @compte",
            p =>
            {
                p.AddWithValue("entite", entite);
                p.AddWithValue("compte", compte);
            },
            Lire,
            cancellationToken).ConfigureAwait(false)).SingleOrDefault();

    public Task<IReadOnlyList<CompteLu>> LireComptesAsync(string entite, CancellationToken cancellationToken) =>
        Requete.LireAsync(
            source,
            $"SELECT {Colonnes} FROM projections.chart_of_accounts WHERE entity_id = @entite ORDER BY account_id COLLATE \"C\"",
            p => p.AddWithValue("entite", entite),
            Lire,
            cancellationToken);

    private static CompteLu Lire(NpgsqlDataReader l) =>
        new(l.GetString(0), l.GetString(1), l.GetString(2), l.GetString(3), l.GetString(4), l.GetBoolean(5));
}

public sealed class NpgsqlLecteurSoldes(NpgsqlDataSource source) : ILecteurSoldes
{
    public Task<IReadOnlyList<CumulCompte>> LireSoldesAsync(string entite, string? compte, CancellationToken cancellationToken) =>
        Requete.LireAsync(
            source,
            $"""
            SELECT account_id, currency, debit_total, credit_total
            FROM projections.account_balances
            WHERE entity_id = @entite {(compte is null ? string.Empty : "AND account_id = @compte")}
            ORDER BY account_id COLLATE "C", currency COLLATE "C"
            """,
            p =>
            {
                p.AddWithValue("entite", entite);
                if (compte is not null)
                {
                    p.AddWithValue("compte", compte);
                }
            },
            l => new CumulCompte(l.GetString(0), l.GetString(1), l.GetDecimal(2), l.GetDecimal(3)),
            cancellationToken);
}

/// <summary>Cumuls à une date d'affaires : agrégat de <c>ledger_lines</c> par l'index <c>ledger_lines_account_order</c>.</summary>
public sealed class NpgsqlLecteurMouvements(NpgsqlDataSource source) : ILecteurMouvements
{
    public Task<IReadOnlyList<CumulCompte>> CumulerAsync(string entite, string? compte, DateOnly auPlusTard, CancellationToken cancellationToken) =>
        Requete.LireAsync(
            source,
            $"""
            SELECT account_id, currency,
                   coalesce(sum(amount) FILTER (WHERE side = 'Debit'), 0),
                   coalesce(sum(amount) FILTER (WHERE side = 'Credit'), 0)
            FROM projections.ledger_lines
            WHERE entity_id = @entite {(compte is null ? string.Empty : "AND account_id = @compte")} AND business_date <= @date
            GROUP BY account_id, currency
            ORDER BY account_id COLLATE "C", currency COLLATE "C"
            """,
            p =>
            {
                p.AddWithValue("entite", entite);
                p.AddWithValue("date", auPlusTard);
                if (compte is not null)
                {
                    p.AddWithValue("compte", compte);
                }
            },
            l => new CumulCompte(l.GetString(0), l.GetString(1), l.GetDecimal(2), l.GetDecimal(3)),
            cancellationToken);
}

/// <summary>Sous-livre : comparaison de valeurs de ligne sur l'ordre de l'index, sans décalage (<c>OFFSET</c>).</summary>
public sealed class NpgsqlLecteurLignesCompte(NpgsqlDataSource source) : ILecteurLignesCompte
{
    public Task<IReadOnlyList<LigneCompte>> LireLignesAsync(string entite, string compte, PositionLigne? apres, int nombre, CancellationToken cancellationToken) =>
        Requete.LireAsync(
            source,
            $"""
            SELECT l.entry_id, l.line_number, l.business_date, l.period, l.side, l.amount, l.currency, l.global_sequence, e.description
            FROM projections.ledger_lines l
            JOIN projections.journal_entries e ON e.entry_id = l.entry_id
            WHERE l.entity_id = @entite AND l.account_id = @compte
            {(apres is null ? string.Empty : "AND (l.business_date, l.global_sequence, l.line_number) > (@date, @sequence, @ligne)")}
            ORDER BY l.business_date, l.global_sequence, l.line_number
            LIMIT @nombre
            """,
            p =>
            {
                p.AddWithValue("entite", entite);
                p.AddWithValue("compte", compte);
                p.AddWithValue("nombre", nombre);
                if (apres is not null)
                {
                    p.AddWithValue("date", apres.DateAffaires);
                    p.AddWithValue("sequence", apres.Sequence);
                    p.AddWithValue("ligne", apres.NumeroLigne);
                }
            },
            l => new LigneCompte(
                l.GetGuid(0),
                l.GetInt32(1),
                l.GetFieldValue<DateOnly>(2),
                l.GetString(3),
                l.GetString(4),
                l.GetDecimal(5),
                l.GetString(6),
                l.GetInt64(7),
                l.TexteOuNul(8)),
            cancellationToken);
}

public sealed class NpgsqlLecteurEcritures(NpgsqlDataSource source) : ILecteurEcritures
{
    public async Task<EcritureLue?> TrouverEcritureAsync(Guid ecriture, CancellationToken cancellationToken)
    {
        var lignes = await Requete.LireAsync(
            source,
            "SELECT line_number, account_id, side, amount, currency FROM projections.ledger_lines WHERE entry_id = @ecriture ORDER BY line_number",
            p => p.AddWithValue("ecriture", ecriture),
            l => new LigneEcritureLue(l.GetInt32(0), l.GetString(1), l.GetString(2), l.GetDecimal(3), l.GetString(4)),
            cancellationToken).ConfigureAwait(false);
        var entetes = await Requete.LireAsync(
            source,
            """
            SELECT entry_id, entity_id, business_date, period, description, reversal_of, reversed_by, global_sequence, recorded_at, actor
            FROM projections.journal_entries
            WHERE entry_id = @ecriture
            """,
            p => p.AddWithValue("ecriture", ecriture),
            l => new EcritureLue(
                l.GetGuid(0),
                l.GetString(1),
                l.GetFieldValue<DateOnly>(2),
                l.GetString(3),
                l.TexteOuNul(4),
                l.GuidOuNul(5),
                l.GuidOuNul(6),
                l.GetInt64(7),
                l.GetFieldValue<DateTimeOffset>(8),
                l.GetString(9),
                lignes),
            cancellationToken).ConfigureAwait(false);
        return entetes.SingleOrDefault();
    }
}

public sealed class NpgsqlLecteurPeriodes(NpgsqlDataSource source) : ILecteurPeriodes
{
    public async Task<PeriodeLue?> TrouverPeriodeAsync(string entite, string periode, CancellationToken cancellationToken) =>
        (await Requete.LireAsync(
            source,
            """
            SELECT entity_id, period, status, version, opened_at, sealed_at, event_count, merkle_root
            FROM projections.periods
            WHERE entity_id = @entite AND period = @periode
            """,
            p =>
            {
                p.AddWithValue("entite", entite);
                p.AddWithValue("periode", periode);
            },
            l => new PeriodeLue(l.GetString(0), l.GetString(1), l.GetString(2), l.GetInt64(3), l.InstantOuNul(4), l.InstantOuNul(5), l.EntierOuNul(6), l.TexteOuNul(7)),
            cancellationToken).ConfigureAwait(false)).SingleOrDefault();
}

public sealed class NpgsqlLecteurInstantanes(NpgsqlDataSource source) : ILecteurInstantanes
{
    public async Task<InstantanePeriode?> TrouverInstantaneAsync(string entite, string periode, CancellationToken cancellationToken)
    {
        var lignes = await Requete.LireAsync(
            source,
            """
            SELECT account_id, currency, debits, credits, seal_sequence, event_count
            FROM projections.period_snapshots
            WHERE entity_id = @entite AND period = @periode
            ORDER BY account_id COLLATE "C", currency COLLATE "C"
            """,
            p =>
            {
                p.AddWithValue("entite", entite);
                p.AddWithValue("periode", periode);
            },
            l => (Mouvement: new CumulCompte(l.GetString(0), l.GetString(1), l.GetDecimal(2), l.GetDecimal(3)), Sceau: l.GetInt64(4), Nombre: l.GetInt64(5)),
            cancellationToken).ConfigureAwait(false);
        return lignes.Count == 0 ? null : new InstantanePeriode(lignes[0].Sceau, lignes[0].Nombre, [.. lignes.Select(l => l.Mouvement)]);
    }
}

public sealed class NpgsqlLecteurTaxes(NpgsqlDataSource source) : ILecteurTaxes
{
    public Task<IReadOnlyList<TaxesPeriode>> LireTaxesAsync(string entite, string periode, CancellationToken cancellationToken) =>
        Requete.LireAsync(
            source,
            """
            SELECT currency, gst_collected, gst_paid, qst_collected, qst_paid
            FROM projections.tax_projection
            WHERE entity_id = @entite AND period = @periode
            ORDER BY currency COLLATE "C"
            """,
            p =>
            {
                p.AddWithValue("entite", entite);
                p.AddWithValue("periode", periode);
            },
            l => new TaxesPeriode(l.GetString(0), l.GetDecimal(1), l.GetDecimal(2), l.GetDecimal(3), l.GetDecimal(4)),
            cancellationToken);
}

/// <summary>
/// Repères de fraîcheur en une requête : plus petite position des projections (absente = 0),
/// tête du journal et premier événement au-delà de cette position (index de clé primaire de
/// chaque partition de <c>ledger.events</c>).
/// </summary>
public sealed class NpgsqlLecteurReperesFraicheur(NpgsqlDataSource source) : ILecteurReperesFraicheur
{
    public async Task<ReperesFraicheur> LireAsync(IReadOnlyCollection<string> projections, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(projections);
        var lus = await Requete.LireAsync(
            source,
            """
            WITH position AS (
                SELECT coalesce(min(coalesce(p.through_sequence, 0)), 0) AS jusqua
                FROM unnest(@projections::text[]) AS n(slot)
                LEFT JOIN projections.projection_positions p ON p.slot = n.slot
            )
            SELECT position.jusqua,
                   coalesce((SELECT e.global_sequence FROM ledger.events e ORDER BY e.global_sequence DESC LIMIT 1), 0),
                   (SELECT e.recorded_at FROM ledger.events e WHERE e.global_sequence > position.jusqua ORDER BY e.global_sequence LIMIT 1)
            FROM position
            """,
            p => p.AddWithValue("projections", projections.ToArray()),
            l => new ReperesFraicheur(l.GetInt64(0), l.GetInt64(1), l.InstantOuNul(2)),
            cancellationToken).ConfigureAwait(false);
        return lus.Single();
    }
}
