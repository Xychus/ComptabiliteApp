using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Processing;
using Ledger.Projections.Application.Rebuild;
using Ledger.Projections.Infrastructure.Tests.Postgres;
using Ledger.Projections.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using static Ledger.Projections.Tests.Support.JournalBuilder;

namespace Ledger.Projections.Infrastructure.Tests.Contract;

/// <summary>Suite de contrat contre PostgreSQL (Testcontainers), avec le rôle <c>ledger_projection</c>.</summary>
[Collection(PostgresGroup.Name)]
[Trait("Category", "Integration")]
public sealed class NpgsqlProjectionStoreTests(PostgresFixture database) : ProjectionStoreContract
{
    protected override ProjectionBackend CreateBackend() => new NpgsqlBackend(database);

    protected override async Task<(decimal, decimal, decimal, decimal)?> TaxAsync(string period)
    {
        await using var command = database.OwnerDataSource.CreateCommand(
            "SELECT gst_collected, gst_paid, qst_collected, qst_paid FROM projections.tax_projection WHERE entity_id = @entity AND period = @period AND currency = 'CAD'");
        command.Parameters.AddWithValue("entity", Backend.Entity);
        command.Parameters.AddWithValue("period", period);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        return await reader.ReadAsync(Ct) ? (reader.GetDecimal(0), reader.GetDecimal(1), reader.GetDecimal(2), reader.GetDecimal(3)) : null;
    }

    /// <summary>Accès PostgreSQL de la suite : journal écrit par le propriétaire, projections par <c>ledger_projection</c>.</summary>
    internal sealed class NpgsqlBackend(PostgresFixture database) : ProjectionBackend
    {
        public override Task<EventEnvelopeV1> RecordAsync(EventEnvelopeV1 envelope) => database.AppendAsync(envelope);

        public override async Task<string> DumpAsync(string projection, string entityId)
        {
            var lines = new List<string>();
            foreach (var table in ProjectionTables.Of(projection))
            {
                var sql = table.EndsWith(ProjectionTables.AppliedSuffix, StringComparison.Ordinal)
                    ? $"SELECT a.event_id::text || ' => ' || a.global_sequence FROM projections.\"{table}\" a JOIN ledger.events e ON e.event_id = a.event_id WHERE e.entity_id = @entity"
                    : $"SELECT row_to_json(t)::text FROM projections.\"{table}\" t WHERE t.entity_id = @entity";
                lines.Add($"[{table}]");
                lines.AddRange((await QueryAsync(sql, entityId)).Order(StringComparer.Ordinal));
            }

            return string.Join("\n", lines);
        }

        public override async Task<long?> PositionAsync(string slot)
        {
            await using var command = database.OwnerDataSource.CreateCommand("SELECT through_sequence FROM projections.projection_positions WHERE slot = @slot");
            command.Parameters.AddWithValue("slot", slot);
            return (long?)await command.ExecuteScalarAsync(Ct);
        }

        public override async Task<(decimal Debit, decimal Credit)?> BalanceAsync(string entityId, string accountId)
        {
            await using var command = database.OwnerDataSource.CreateCommand(
                "SELECT debit_total, credit_total FROM projections.account_balances WHERE entity_id = @entity AND account_id = @account AND currency = 'CAD'");
            command.Parameters.AddWithValue("entity", entityId);
            command.Parameters.AddWithValue("account", accountId);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            return await reader.ReadAsync(Ct) ? (reader.GetDecimal(0), reader.GetDecimal(1)) : null;
        }

        protected override void AddStore(IServiceCollection services) => services.AddPostgresProjectionStore(_ => database.ProjectionConnectionString);

        private async Task<List<string>> QueryAsync(string sql, string entityId)
        {
            await using var command = database.OwnerDataSource.CreateCommand(sql);
            command.Parameters.AddWithValue("entity", entityId);
            await using var reader = await command.ExecuteReaderAsync(Ct);
            var rows = new List<string>();
            while (await reader.ReadAsync(Ct))
            {
                rows.Add(reader.GetString(0));
            }

            return rows;
        }
    }
}

/// <summary>Garanties propres à PostgreSQL : verrou de bascule, droits, validation hors ordre de séquence.</summary>
[Collection(PostgresGroup.Name)]
[Trait("Category", "Integration")]
public sealed class NpgsqlProjectionTests(PostgresFixture database)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Les_tables_reconstruites_appartiennent_au_worker_et_restent_lisibles_par_l_API_de_requete()
    {
        await using var backend = new NpgsqlProjectionStoreTests.NpgsqlBackend(database);
        await backend.DeliverAsync(Scenario.Build(backend.Entity, backend.OtherEntity));

        await backend.RebuildAsync(ProjectionNames.LedgerEntries);

        foreach (var table in ProjectionTables.Of(ProjectionNames.LedgerEntries))
        {
            Assert.Equal("ledger_projection", await ScalarAsync<string>($"SELECT tableowner FROM pg_tables WHERE schemaname = 'projections' AND tablename = '{table}'"));
            Assert.True(await ScalarAsync<bool>($"SELECT has_table_privilege('ledger_query', 'projections.{table}', 'SELECT')"));
            Assert.False(await ScalarAsync<bool>($"SELECT has_table_privilege('ledger_query', 'projections.{table}', 'INSERT')"));
        }

        Assert.Equal(0L, await ScalarAsync<long>("SELECT count(*) FROM pg_tables WHERE schemaname = 'projections' AND (tablename LIKE '%\\_\\_rebuild' OR tablename LIKE '%\\_\\_retired')"));
    }

    [Fact]
    public async Task Le_consommateur_attend_la_bascule_puis_ecrit_dans_les_nouvelles_tables()
    {
        await using var backend = new NpgsqlProjectionStoreTests.NpgsqlBackend(database);
        var journal = new JournalBuilder(backend.Entity);
        await backend.DeliverAsync(journal.Posted(new DateOnly(2026, 3, 2), Debit("1000", 10m), Credit("4000", 10m)));
        var next = await backend.RecordAllAsync([journal.Posted(new DateOnly(2026, 3, 3), Debit("1000", 1m), Credit("4000", 1m))]);

        // Bascule en cours : verrou exclusif tenu, tables parallèles prêtes mais pas encore en service.
        var scope = backend.Services.CreateAsyncScope();
        await using (scope)
        {
            var services = scope.ServiceProvider;
            const string projection = ProjectionNames.AccountBalances;
            await services.GetRequiredService<IProjectionSession>().BeginAsync(ProjectionTarget.Rebuild(projection), Ct);
            var rebuild = services.GetRequiredService<IProjectionRebuildStore>();
            await rebuild.PrepareAsync(projection, Ct);
            await rebuild.LockLiveAsync(projection, Ct);

            var delivery = backend.RedeliverAsync([.. next]);
            await Task.Delay(TimeSpan.FromMilliseconds(500), Ct);
            Assert.False(delivery.IsCompleted, "Le consommateur devait attendre le verrou de bascule.");

            await rebuild.SwapAsync(projection, Ct);
            await services.GetRequiredService<IProjectionSession>().CommitAsync(Ct);
            await delivery;
        }

        // Les nouvelles tables (vides au départ) ne contiennent que l'événement livré après la bascule.
        Assert.Equal((1m, 0m), await backend.BalanceAsync(backend.Entity, "1000"));
    }

    [Fact]
    public async Task Un_evenement_valide_apres_un_plus_recent_est_projete_et_rattrape_par_le_rejeu()
    {
        await using var backend = new NpgsqlProjectionStoreTests.NpgsqlBackend(database);
        var journal = new JournalBuilder(backend.Entity);
        await using var slow = await database.OwnerDataSource.OpenConnectionAsync(Ct);
        await using var slowTransaction = await slow.BeginTransactionAsync(Ct);

        // La transaction lente obtient la plus petite séquence mais valide en dernier (ADR 0007, point 6).
        var straggler = await database.AppendAsync(journal.Posted(new DateOnly(2026, 3, 2), Debit("1000", 2m), Credit("4000", 2m)), slow, slowTransaction);
        var fast = await backend.DeliverAsync(journal.Posted(new DateOnly(2026, 3, 3), Debit("1000", 4m), Credit("4000", 4m)));
        await slowTransaction.CommitAsync(Ct);
        await backend.RedeliverAsync(straggler);

        Assert.True(straggler.GlobalSequence < fast[0].GlobalSequence);
        Assert.Equal((6m, 0m), await backend.BalanceAsync(backend.Entity, "1000"));
        Assert.True(await backend.PositionAsync(ProjectionNames.AccountBalances) >= fast[0].GlobalSequence);

        var incremental = await backend.DumpAsync(ProjectionNames.AccountBalances, backend.Entity);
        await backend.RebuildAsync(ProjectionNames.AccountBalances);
        Assert.Equal(incremental, await backend.DumpAsync(ProjectionNames.AccountBalances, backend.Entity));
    }

    [Fact]
    public async Task Le_rattrapage_sous_verrou_lit_les_evenements_absents_des_tables_paralleles()
    {
        await using var backend = new NpgsqlProjectionStoreTests.NpgsqlBackend(database);
        var journal = new JournalBuilder(backend.Entity);
        var stored = await backend.DeliverAsync(
            journal.Posted(new DateOnly(2026, 3, 2), Debit("1000", 1m), Credit("4000", 1m)),
            journal.Opened("2026-03"));
        var scope = backend.Services.CreateAsyncScope();
        await using (scope)
        {
            const string projection = ProjectionNames.AccountBalances;
            await scope.ServiceProvider.GetRequiredService<IProjectionSession>().BeginAsync(ProjectionTarget.Rebuild(projection), Ct);
            var rebuild = scope.ServiceProvider.GetRequiredService<IProjectionRebuildStore>();
            await rebuild.PrepareAsync(projection, Ct);

            var missing = await rebuild.ReadUnappliedAsync(projection, [EventTypes.JournalEntryPosted], Ct);

            Assert.Contains(missing, e => e.EventId == stored[0].EventId);
            Assert.DoesNotContain(missing, e => e.EventId == stored[1].EventId);
            var read = missing.Single(e => e.EventId == stored[0].EventId);
            Assert.Equal(stored[0] with { Payload = default }, read with { Payload = default });
            Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(stored[0].Payload.GetRawText()),
                System.Text.Json.Nodes.JsonNode.Parse(read.Payload.GetRawText())));
        }
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var command = database.OwnerDataSource.CreateCommand(sql);
        return (T)(await command.ExecuteScalarAsync(Ct))!;
    }
}
