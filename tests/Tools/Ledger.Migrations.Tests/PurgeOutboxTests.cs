using Npgsql;

namespace Ledger.Migrations.Tests;

/// <summary>Publication de l'outbox et purge des lignes déjà décodables par le slot (ADR 0020).</summary>
[Collection(PostgresGroup.Name)]
[Trait("Category", "Integration")]
public sealed class PurgeOutboxTests(PostgresFixture database)
{
    private const string RetentionInvalide = "LG004";
    private const string SlotAbsent = "LG005";
    private const string PrivilegeInsuffisant = "42501";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task La_publication_ne_publie_que_les_insertions()
    {
        await using var connexion = await OuvrirAsync(database.OwnerConnectionString);
        await using var commande = new NpgsqlCommand("SELECT pubinsert, pubupdate, pubdelete, pubtruncate FROM pg_publication WHERE pubname = 'ledger_pub'", connexion);
        await using var lecteur = await commande.ExecuteReaderAsync(Ct);

        Assert.True(await lecteur.ReadAsync(Ct));
        Assert.Equal([true, false, false, false], [lecteur.GetBoolean(0), lecteur.GetBoolean(1), lecteur.GetBoolean(2), lecteur.GetBoolean(3)]);
    }

    [Fact]
    public async Task La_purge_supprime_seulement_les_lignes_plus_anciennes_que_la_retention()
    {
        var slot = "essai_" + Guid.NewGuid().ToString("N");
        await ExecuterAsync(database.OwnerConnectionString, $"SELECT pg_create_logical_replication_slot('{slot}', 'pgoutput')");
        try
        {
            var ancienne = await InsererOutboxAsync(TimeSpan.FromDays(8));
            var recente = await InsererOutboxAsync(TimeSpan.FromHours(1));

            var supprimees = await ScalaireAsync<long>(database.ChaineMaintenance, $"SELECT ledger.purger_outbox(INTERVAL '7 days', '{slot}')");

            Assert.True(supprimees >= 1);
            Assert.False(await ExisteAsync(ancienne));
            Assert.True(await ExisteAsync(recente));
        }
        finally
        {
            await ExecuterAsync(database.OwnerConnectionString, $"SELECT pg_drop_replication_slot('{slot}')");
        }
    }

    [Fact]
    public async Task La_purge_est_refusee_sans_slot_de_replication()
    {
        var ancienne = await InsererOutboxAsync(TimeSpan.FromDays(8));

        var erreur = await Assert.ThrowsAsync<PostgresException>(() => ScalaireAsync<long>(database.ChaineMaintenance, "SELECT ledger.purger_outbox(INTERVAL '7 days', 'slot_inexistant')"));

        Assert.Equal(SlotAbsent, erreur.SqlState);
        Assert.True(await ExisteAsync(ancienne));
    }

    [Theory]
    [InlineData("INTERVAL '59 minutes'")]
    [InlineData("INTERVAL '-1 day'")]
    [InlineData("NULL")]
    public async Task La_purge_refuse_une_retention_de_moins_d_une_heure(string retention)
    {
        var erreur = await Assert.ThrowsAsync<PostgresException>(() => ScalaireAsync<long>(database.ChaineMaintenance, $"SELECT ledger.purger_outbox({retention}, 'ledger_outbox')"));

        Assert.Equal(RetentionInvalide, erreur.SqlState);
    }

    [Fact]
    public async Task Le_role_applicatif_ne_peut_pas_purger()
    {
        var erreur = await Assert.ThrowsAsync<PostgresException>(() => ScalaireAsync<long>(database.AppConnectionString, "SELECT ledger.purger_outbox(INTERVAL '7 days', 'ledger_outbox')"));

        Assert.Equal(PrivilegeInsuffisant, erreur.SqlState);
    }

    [Fact]
    public async Task Le_role_de_maintenance_ne_peut_rien_faire_d_autre()
    {
        var erreur = await Assert.ThrowsAsync<PostgresException>(() => ExecuterAsync(database.ChaineMaintenance, "DELETE FROM ledger.outbox"));

        Assert.Equal(PrivilegeInsuffisant, erreur.SqlState);
    }

    private async Task<Guid> InsererOutboxAsync(TimeSpan age)
    {
        var evenement = Guid.CreateVersion7();
        await using var connexion = await OuvrirAsync(database.OwnerConnectionString);
        await using var commande = new NpgsqlCommand(
            "INSERT INTO ledger.outbox (event_id, topic, partition_key, payload, created_at) VALUES (@id, 'ledger.events', 'PURGE', '{}', now() - @age)",
            connexion);
        commande.Parameters.AddWithValue("id", evenement);
        commande.Parameters.AddWithValue("age", age);
        await commande.ExecuteNonQueryAsync(Ct);
        return evenement;
    }

    private async Task<bool> ExisteAsync(Guid evenement) =>
        await ScalaireAsync<bool>(database.OwnerConnectionString, $"SELECT EXISTS (SELECT FROM ledger.outbox WHERE event_id = '{evenement}')");

    private static async Task<NpgsqlConnection> OuvrirAsync(string chaine)
    {
        var connexion = new NpgsqlConnection(chaine);
        await connexion.OpenAsync(Ct);
        return connexion;
    }

    private static async Task ExecuterAsync(string chaine, string sql)
    {
        await using var connexion = await OuvrirAsync(chaine);
        await using var commande = new NpgsqlCommand(sql, connexion);
        await commande.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<T> ScalaireAsync<T>(string chaine, string sql)
    {
        await using var connexion = await OuvrirAsync(chaine);
        await using var commande = new NpgsqlCommand(sql, connexion);
        return (T)(await commande.ExecuteScalarAsync(Ct))!;
    }
}
