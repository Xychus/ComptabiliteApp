using Npgsql;

namespace Ledger.Migrations.Tests;

/// <summary>Schéma du journal et garanties en base (ADR 0005 et 0007).</summary>
[Collection(PostgresGroup.Name)]
[Trait("Category", "Integration")]
public sealed class SchemaTests(PostgresFixture database)
{
    private const string AppendOnlyViolation = "LG001";
    private const string PeriodSealedViolation = "LG002";
    private const string SealCountMismatch = "LG003";
    private const string InsufficientPrivilege = "42501";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Les_migrations_s_appliquent_dans_l_ordre()
    {
        Assert.True(database.FirstRun.Successful, database.FirstRun.Error?.ToString());
        Assert.Equal(
            ["0001_journal", "0002_append_only_et_sceau", "0003_outbox", "0004_referentiel", "0005_roles_et_publication", "0006_sceau_avec_nombre_d_evenements", "0007_projections", "0008_publication_et_purge_outbox"],
            database.FirstRun.ExecutedScripts.Select(name => name.Split('.')[^2]));
    }

    [Fact]
    public void Une_seconde_execution_n_applique_rien()
    {
        var second = LedgerMigrator.Upgrade(database.OwnerConnectionString, logToConsole: false);

        Assert.True(second.Successful, second.Error?.ToString());
        Assert.Empty(second.ExecutedScripts);
    }

    [Fact]
    public async Task Le_journal_est_partitionne_par_annee_de_2020_a_2039() =>
        Assert.Equal(20L, await ScalarAsync<long>(database.OwnerConnectionString, "SELECT count(*) FROM pg_inherits WHERE inhparent = 'ledger.events'::regclass"));

    [Fact]
    public async Task Un_evenement_est_range_dans_la_partition_de_son_annee()
    {
        var sequence = await InsertEventAsync(database.OwnerConnectionString, period: "2027-11");

        Assert.Equal(
            "ledger.events_2027",
            await ScalarAsync<string>(database.OwnerConnectionString, $"SELECT tableoid::regclass::text FROM ledger.events WHERE global_sequence = {sequence}"));
    }

    [Fact]
    public async Task Une_periode_hors_des_partitions_est_refusee()
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertEventAsync(database.OwnerConnectionString, period: "2019-12"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task La_sequence_globale_fournie_en_entree_est_ignoree()
    {
        var sequence = await InsertEventAsync(database.AppConnectionString, globalSequence: 9_000_000_000);

        Assert.NotEqual(9_000_000_000, sequence);
    }

    [Theory]
    [InlineData("UPDATE ledger.events SET actor = 'pirate' WHERE global_sequence = {0}")]
    [InlineData("DELETE FROM ledger.events WHERE global_sequence = {0}")]
    [InlineData("UPDATE ledger.stream_versions SET hash = repeat('0', 64) WHERE global_sequence = {0}")]
    [InlineData("DELETE FROM ledger.stream_versions WHERE global_sequence = {0}")]
    [InlineData("TRUNCATE ledger.events CASCADE")]
    [InlineData("TRUNCATE ledger.events_2026 CASCADE")]
    [InlineData("TRUNCATE ledger.stream_versions CASCADE")]
    [InlineData("TRUNCATE ledger.entry_ids")]
    public async Task Le_journal_refuse_toute_modification_meme_au_proprietaire(string statement)
    {
        var sequence = await InsertEventAsync(database.OwnerConnectionString);

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database.OwnerConnectionString, string.Format(System.Globalization.CultureInfo.InvariantCulture, statement, sequence)));

        Assert.Equal(AppendOnlyViolation, error.SqlState);
    }

    [Fact]
    public async Task La_table_entry_ids_refuse_la_modification_meme_au_proprietaire()
    {
        var entryId = Guid.CreateVersion7();
        await InsertEventAsync(database.OwnerConnectionString, claimedEntryId: entryId);

        var update = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database.OwnerConnectionString, $"UPDATE ledger.entry_ids SET entry_id = gen_random_uuid() WHERE entry_id = '{entryId}'"));
        var delete = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database.OwnerConnectionString, $"DELETE FROM ledger.entry_ids WHERE entry_id = '{entryId}'"));

        Assert.Equal(AppendOnlyViolation, update.SqlState);
        Assert.Equal(AppendOnlyViolation, delete.SqlState);
    }

    [Theory]
    [InlineData("UPDATE ledger.events SET actor = 'pirate' WHERE global_sequence = {0}")]
    [InlineData("DELETE FROM ledger.events WHERE global_sequence = {0}")]
    [InlineData("TRUNCATE ledger.events")]
    [InlineData("UPDATE ledger.events_2026 SET actor = 'pirate' WHERE global_sequence = {0}")]
    [InlineData("DELETE FROM ledger.stream_versions WHERE global_sequence = {0}")]
    [InlineData("DELETE FROM ledger.outbox")]
    [InlineData("UPDATE ledger.periods SET ends_on = starts_on")]
    [InlineData("INSERT INTO projections.account_balances VALUES ('E', '1000', 'CAD', 0, 0)")]
    public async Task Le_role_applicatif_n_a_ni_UPDATE_ni_DELETE_ni_TRUNCATE(string statement)
    {
        var sequence = await InsertEventAsync(database.AppConnectionString);

        var error = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(database.AppConnectionString, string.Format(System.Globalization.CultureInfo.InvariantCulture, statement, sequence)));

        Assert.Equal(InsufficientPrivilege, error.SqlState);
    }

    [Fact]
    public async Task Aucune_insertion_dans_une_periode_scellee()
    {
        var entity = "SCEAU-" + Guid.NewGuid().ToString("N");
        await InsertEventAsync(database.AppConnectionString, entity: entity, eventType: "PeriodSealed", payload: """{"eventCount":0}""");

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertEventAsync(database.AppConnectionString, entity: entity));

        Assert.Equal(PeriodSealedViolation, error.SqlState);
        await InsertEventAsync(database.AppConnectionString, entity: entity, period: "2026-04");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"eventCount":"2"}""")]
    [InlineData("""{"eventCount":1}""")]
    [InlineData("""{"eventCount":3}""")]
    [InlineData("""{"eventCount":2.5}""")]
    public async Task Un_sceau_qui_ne_compte_pas_les_evenements_de_la_periode_est_refuse(string payload)
    {
        var entity = "COMPTE-" + Guid.NewGuid().ToString("N");
        await InsertEventAsync(database.AppConnectionString, entity: entity);
        await InsertEventAsync(database.AppConnectionString, entity: entity);
        await InsertEventAsync(database.AppConnectionString, entity: entity, period: "2026-04");

        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertEventAsync(database.AppConnectionString, entity: entity, eventType: "PeriodSealed", payload: payload));

        Assert.Equal(SealCountMismatch, error.SqlState);
    }

    [Fact]
    public async Task Un_sceau_qui_compte_exactement_les_evenements_de_la_periode_est_accepte()
    {
        var entity = "COMPTE-" + Guid.NewGuid().ToString("N");
        await InsertEventAsync(database.AppConnectionString, entity: entity);
        await InsertEventAsync(database.AppConnectionString, entity: entity);
        await InsertEventAsync(database.AppConnectionString, entity: entity + "-B");

        await InsertEventAsync(database.AppConnectionString, entity: entity, eventType: "PeriodSealed", payload: """{"eventCount":2}""");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public async Task Le_chainage_commence_a_la_version_1(int version, bool withPreviousHash)
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertEventAsync(database.OwnerConnectionString, version: version, previousHash: withPreviousHash ? new string('a', 64) : null));

        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("events_chain_start", error.ConstraintName);
    }

    [Fact]
    public async Task La_date_d_affaires_appartient_a_la_periode()
    {
        var error = await Assert.ThrowsAsync<PostgresException>(() => InsertEventAsync(database.OwnerConnectionString, period: "2026-04", businessDate: new DateOnly(2026, 3, 31)));

        Assert.Equal("events_business_date_in_period", error.ConstraintName);
    }

    [Fact]
    public async Task Les_roles_sont_crees_sans_connexion_et_debezium_peut_repliquer()
    {
        Assert.False(await ScalarAsync<bool>(database.OwnerConnectionString, "SELECT rolcanlogin FROM pg_roles WHERE rolname = 'ledger_projection'"));
        Assert.False(await ScalarAsync<bool>(database.OwnerConnectionString, "SELECT rolcanlogin FROM pg_roles WHERE rolname = 'debezium'"));
        Assert.True(await ScalarAsync<bool>(database.OwnerConnectionString, "SELECT rolreplication FROM pg_roles WHERE rolname = 'debezium'"));
        Assert.True(await ScalarAsync<bool>(database.OwnerConnectionString, "SELECT has_table_privilege('ledger_projection', 'ledger.events', 'SELECT')"));
        Assert.False(await ScalarAsync<bool>(database.OwnerConnectionString, "SELECT has_table_privilege('ledger_projection', 'ledger.events', 'INSERT')"));
        Assert.True(await ScalarAsync<bool>(database.OwnerConnectionString, "SELECT has_table_privilege('debezium', 'ledger.outbox', 'SELECT')"));
    }

    [Fact]
    public async Task Le_plan_comptable_de_reference_est_remplace_par_sa_projection()
    {
        Assert.True(await ScalarAsync<bool>(database.OwnerConnectionString, "SELECT to_regclass('ledger.accounts') IS NULL"));
        Assert.False(await ScalarAsync<bool>(database.OwnerConnectionString, "SELECT to_regclass('projections.chart_of_accounts') IS NULL"));
    }

    [Fact]
    public async Task Les_projections_appartiennent_au_worker_et_sont_en_lecture_seule_pour_l_API_de_requete()
    {
        Assert.Equal(
            await ScalarAsync<long>(database.OwnerConnectionString, "SELECT count(*) FROM pg_tables WHERE schemaname = 'projections'"),
            await ScalarAsync<long>(database.OwnerConnectionString, "SELECT count(*) FROM pg_tables WHERE schemaname = 'projections' AND tableowner = 'ledger_projection'"));
        Assert.Equal(15L, await ScalarAsync<long>(database.OwnerConnectionString, "SELECT count(*) FROM pg_tables WHERE schemaname = 'projections'"));
        Assert.True(await ScalarAsync<bool>(database.OwnerConnectionString, "SELECT has_schema_privilege('ledger_projection', 'projections', 'CREATE')"));
        Assert.False(await ScalarAsync<bool>(database.OwnerConnectionString, "SELECT rolcanlogin FROM pg_roles WHERE rolname = 'ledger_query'"));
        Assert.False(await ScalarAsync<bool>(database.OwnerConnectionString, "SELECT has_schema_privilege('ledger_query', 'projections', 'CREATE')"));
        Assert.True(await ScalarAsync<bool>(database.OwnerConnectionString, "SELECT has_table_privilege('ledger_query', 'projections.account_balances', 'SELECT')"));
        Assert.False(await ScalarAsync<bool>(database.OwnerConnectionString, "SELECT has_table_privilege('ledger_query', 'projections.account_balances', 'INSERT, UPDATE, DELETE, TRUNCATE')"));
        Assert.True(await ScalarAsync<bool>(database.OwnerConnectionString, "SELECT has_table_privilege('ledger_query', 'ledger.events', 'SELECT')"));
        Assert.False(await ScalarAsync<bool>(database.OwnerConnectionString, "SELECT has_table_privilege('ledger_query', 'ledger.events', 'INSERT')"));
        Assert.False(await ScalarAsync<bool>(database.OwnerConnectionString, "SELECT has_schema_privilege('ledger_app', 'projections', 'USAGE')"));
    }

    [Fact]
    public async Task La_publication_ledger_pub_porte_sur_l_outbox() =>
        Assert.Equal(
            "ledger.outbox",
            await ScalarAsync<string>(database.OwnerConnectionString, "SELECT schemaname || '.' || tablename FROM pg_publication_tables WHERE pubname = 'ledger_pub'"));

    /// <summary>Insère un événement minimal valide (et sa ligne de flux) ; renvoie sa séquence globale.</summary>
    private static async Task<long> InsertEventAsync(
        string connectionString,
        string? entity = null,
        string period = "2026-03",
        string eventType = "TestEvent",
        int version = 1,
        string? previousHash = null,
        long? globalSequence = null,
        Guid? claimedEntryId = null,
        DateOnly? businessDate = null,
        string payload = "{}")
    {
        var eventId = Guid.CreateVersion7();
        var streamId = "test-" + eventId.ToString("N");
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var transaction = await connection.BeginTransactionAsync(Ct);
        await using var insert = new NpgsqlCommand(
            $"""
            INSERT INTO ledger.events ({(globalSequence is null ? string.Empty : "global_sequence, ")}event_id, stream_id, stream_version, event_type, schema_version, entity_id,
                business_date, recorded_at, period, payload, correlation_id, actor, hash, previous_hash)
            VALUES ({(globalSequence is null ? string.Empty : "@sequence, ")}@event_id, @stream_id, @version, @event_type, 1, @entity,
                @business_date, now(), @period, @payload::jsonb, gen_random_uuid(), 'tests', repeat('a', 64), @previous_hash)
            RETURNING global_sequence
            """,
            connection,
            transaction);
        insert.Parameters.AddWithValue("sequence", globalSequence ?? 0);
        insert.Parameters.AddWithValue("event_id", eventId);
        insert.Parameters.AddWithValue("stream_id", streamId);
        insert.Parameters.AddWithValue("version", (long)version);
        insert.Parameters.AddWithValue("event_type", eventType);
        insert.Parameters.AddWithValue("entity", entity ?? "SCHEMA-TESTS");
        insert.Parameters.AddWithValue("business_date", businessDate ?? DateOnly.ParseExact(period + "-15", "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        insert.Parameters.AddWithValue("period", period);
        insert.Parameters.AddWithValue("payload", payload);
        insert.Parameters.AddWithValue("previous_hash", (object?)previousHash ?? DBNull.Value);
        var sequence = (long)(await insert.ExecuteScalarAsync(Ct))!;

        await using var stream = new NpgsqlCommand(
            """
            INSERT INTO ledger.stream_versions (stream_id, stream_version, event_id, global_sequence, period, hash)
            VALUES (@stream_id, @version, @event_id, @sequence, @period, repeat('a', 64))
            """,
            connection,
            transaction);
        stream.Parameters.AddWithValue("stream_id", streamId);
        stream.Parameters.AddWithValue("version", (long)version);
        stream.Parameters.AddWithValue("event_id", eventId);
        stream.Parameters.AddWithValue("sequence", sequence);
        stream.Parameters.AddWithValue("period", period);
        await stream.ExecuteNonQueryAsync(Ct);

        if (claimedEntryId is { } claimed)
        {
            await using var claim = new NpgsqlCommand("INSERT INTO ledger.entry_ids (entry_id, event_id) VALUES (@entry_id, @event_id)", connection, transaction);
            claim.Parameters.AddWithValue("entry_id", claimed);
            claim.Parameters.AddWithValue("event_id", eventId);
            await claim.ExecuteNonQueryAsync(Ct);
        }

        await transaction.CommitAsync(Ct);
        return sequence;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
