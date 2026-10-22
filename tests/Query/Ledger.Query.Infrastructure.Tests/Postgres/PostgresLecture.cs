using Ledger.Contracts.Events.V1;
using Ledger.Migrations;
using Ledger.Query.Tests.Support;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Ledger.Query.Infrastructure.Tests.Postgres;

/// <summary>
/// PostgreSQL éphémère (Testcontainers), migré une fois pour toute la collection. Les lecteurs se
/// connectent avec le rôle <c>ledger_query</c>, les projections avec <c>ledger_projection</c>
/// (mots de passe aléatoires propres à l'exécution) ; le rôle propriétaire amorce les tables.
/// </summary>
public sealed class PostgresLecture : IAsyncLifetime
{
    /// <summary>Même version majeure que la cible Aurora (point P4 du plan).</summary>
    public const string Image = "postgres:18-alpine";

    private const string InsertionEvenement =
        """
        INSERT INTO ledger.events (event_id, stream_id, stream_version, event_type, schema_version, entity_id, business_date,
            recorded_at, period, payload, balance_after, correlation_id, causation_id, actor, hash, previous_hash)
        VALUES (@event_id, @stream_id, @stream_version, @event_type, @schema_version, @entity_id, @business_date,
            @recorded_at, @period, @payload::jsonb, @balance_after::numeric, @correlation_id, @causation_id, @actor, @hash, @previous_hash)
        RETURNING global_sequence
        """;

    private readonly PostgreSqlContainer _conteneur = new PostgreSqlBuilder(Image).Build();

    public NpgsqlDataSource Proprietaire { get; private set; } = null!;

    public string ConnexionLecture { get; private set; } = string.Empty;

    public string ConnexionProjections { get; private set; } = string.Empty;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        await _conteneur.StartAsync();
        var migration = LedgerMigrator.Upgrade(_conteneur.GetConnectionString(), logToConsole: false);
        if (!migration.Successful)
        {
            throw new InvalidOperationException("Échec des migrations.", migration.Error);
        }

        Proprietaire = NpgsqlDataSource.Create(_conteneur.GetConnectionString());
        ConnexionLecture = await ActiverRoleAsync("ledger_query");
        ConnexionProjections = await ActiverRoleAsync("ledger_projection");
    }

    /// <summary>Insère l'événement dans <c>ledger.events</c> ; la séquence vient du trigger.</summary>
    public async Task<EventEnvelopeV1> AjouterAuJournalAsync(EventEnvelopeV1 enveloppe)
    {
        ArgumentNullException.ThrowIfNull(enveloppe);
        await using var commande = Proprietaire.CreateCommand(InsertionEvenement);
        commande.Parameters.AddWithValue("event_id", enveloppe.EventId);
        commande.Parameters.AddWithValue("stream_id", enveloppe.StreamId);
        commande.Parameters.AddWithValue("stream_version", enveloppe.StreamVersion);
        commande.Parameters.AddWithValue("event_type", enveloppe.EventType);
        commande.Parameters.AddWithValue("schema_version", enveloppe.SchemaVersion);
        commande.Parameters.AddWithValue("entity_id", enveloppe.EntityId);
        commande.Parameters.AddWithValue("business_date", (object?)enveloppe.BusinessDate ?? DBNull.Value);
        commande.Parameters.AddWithValue("recorded_at", enveloppe.RecordedAt);
        commande.Parameters.AddWithValue("period", enveloppe.Period);
        commande.Parameters.AddWithValue("payload", enveloppe.Payload.GetRawText());
        commande.Parameters.AddWithValue("balance_after", (object?)enveloppe.BalanceAfter ?? DBNull.Value);
        commande.Parameters.AddWithValue("correlation_id", enveloppe.CorrelationId);
        commande.Parameters.AddWithValue("causation_id", (object?)enveloppe.CausationId ?? DBNull.Value);
        commande.Parameters.AddWithValue("actor", enveloppe.Actor);
        commande.Parameters.AddWithValue("hash", enveloppe.Hash);
        commande.Parameters.AddWithValue("previous_hash", (object?)enveloppe.PreviousHash ?? DBNull.Value);
        var sequence = (long)(await commande.ExecuteScalarAsync(Ct))!;
        return enveloppe with { GlobalSequence = sequence };
    }

    /// <summary>Écrit les lignes du jeu d'essai dans les tables en service du schéma <c>projections</c>.</summary>
    public async Task AmorcerAsync(JeuEssai jeu)
    {
        ArgumentNullException.ThrowIfNull(jeu);
        await using var lot = Proprietaire.CreateBatch();
        foreach (var c in jeu.Comptes)
        {
            Ajouter(
                lot,
                """
                INSERT INTO projections.chart_of_accounts (entity_id, account_id, name, currency, normal_side, is_active, no_negative_balance, chart_version, defined_at)
                VALUES ($1, $2, $3, $4, $5, $6, false, 1, $7)
                """,
                c.Entite,
                c.Compte,
                c.Nom,
                c.Devise,
                c.SensNormal,
                c.EstActif,
                JeuEssai.Origine);
        }

        foreach (var s in jeu.Soldes)
        {
            Ajouter(lot, "INSERT INTO projections.account_balances (entity_id, account_id, currency, debit_total, credit_total) VALUES ($1, $2, $3, $4, $5)", s.Entite, s.Compte, s.Devise, s.Debits, s.Credits);
        }

        foreach (var e in jeu.Ecritures)
        {
            Ajouter(
                lot,
                """
                INSERT INTO projections.journal_entries (entry_id, entity_id, business_date, period, description, reversal_of, reversed_by, global_sequence, recorded_at, actor)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                """,
                e.Ecriture,
                e.Entite,
                e.DateAffaires,
                e.Periode,
                e.Libelle,
                e.ContrePassationDe,
                e.ContrePasseePar,
                e.Sequence,
                e.EnregistreeLe,
                e.Acteur);
        }

        foreach (var l in jeu.Lignes)
        {
            Ajouter(
                lot,
                """
                INSERT INTO projections.ledger_lines (entry_id, line_number, entity_id, account_id, side, amount, currency, business_date, period, global_sequence)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
                """,
                l.Ecriture,
                l.NumeroLigne,
                l.Entite,
                l.Compte,
                l.Sens,
                l.Montant,
                l.Devise,
                l.DateAffaires,
                l.Periode,
                l.Sequence);
        }

        foreach (var p in jeu.Periodes)
        {
            Ajouter(
                lot,
                """
                INSERT INTO projections.periods (entity_id, period, status, version, opened_at, sealed_at, event_count, merkle_root)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                """,
                p.Entite,
                p.Periode,
                p.Statut,
                p.Version,
                p.OuverteLe,
                p.ScelleeLe,
                p.NombreEvenements,
                p.RacineMerkle);
        }

        foreach (var i in jeu.Instantanes)
        {
            Ajouter(
                lot,
                """
                INSERT INTO projections.period_snapshots (entity_id, period, account_id, currency, debits, credits, seal_sequence, event_count)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
                """,
                i.Entite,
                i.Periode,
                i.Compte,
                i.Devise,
                i.Debits,
                i.Credits,
                i.SequenceSceau,
                i.NombreEvenements);
        }

        foreach (var t in jeu.Taxes)
        {
            Ajouter(
                lot,
                """
                INSERT INTO projections.tax_projection (entity_id, period, currency, gst_collected, gst_paid, qst_collected, qst_paid)
                VALUES ($1, $2, $3, $4, $5, $6, $7)
                """,
                t.Entite,
                t.Periode,
                t.Devise,
                t.TpsPercue,
                t.TpsPayee,
                t.TvqPercue,
                t.TvqPayee);
        }

        if (lot.BatchCommands.Count > 0)
        {
            await lot.ExecuteNonQueryAsync(Ct);
        }
    }

    public async Task PositionnerAsync(string projection, long sequence)
    {
        await using var commande = Proprietaire.CreateCommand(
            """
            INSERT INTO projections.projection_positions (slot, through_sequence, last_recorded_at) VALUES (@slot, @sequence, now())
            ON CONFLICT (slot) DO UPDATE SET through_sequence = EXCLUDED.through_sequence
            """);
        commande.Parameters.AddWithValue("slot", projection);
        commande.Parameters.AddWithValue("sequence", sequence);
        await commande.ExecuteNonQueryAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (Proprietaire is not null)
        {
            await Proprietaire.DisposeAsync();
        }

        await _conteneur.DisposeAsync();
    }

    private static void Ajouter(NpgsqlBatch lot, string sql, params object?[] valeurs)
    {
        var commande = new NpgsqlBatchCommand(sql);
        foreach (var valeur in valeurs)
        {
            commande.Parameters.Add(new NpgsqlParameter { Value = valeur ?? DBNull.Value });
        }

        lot.BatchCommands.Add(commande);
    }

    private async Task<string> ActiverRoleAsync(string role)
    {
        var motDePasse = Guid.NewGuid().ToString("N");
        await using (var commande = Proprietaire.CreateCommand($"ALTER ROLE {role} LOGIN PASSWORD '{motDePasse}'"))
        {
            await commande.ExecuteNonQueryAsync();
        }

        return new NpgsqlConnectionStringBuilder(_conteneur.GetConnectionString()) { Username = role, Password = motDePasse }.ConnectionString;
    }
}

[CollectionDefinition(Nom)]
public sealed class GroupePostgres : ICollectionFixture<PostgresLecture>
{
    public const string Nom = "postgres-lecture";
}
