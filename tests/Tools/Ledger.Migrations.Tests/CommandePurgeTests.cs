using Npgsql;

namespace Ledger.Migrations.Tests;

public sealed class ConfigurationPurgeTests
{
    [Fact]
    public void Lire_en_aws_compose_une_chaine_verify_full_sans_mot_de_passe_et_active_iam()
    {
        var variables = new Dictionary<string, string>
        {
            [PurgeOutbox.VariableHote] = "ledger-dev.cluster-abc.ca-central-1.rds.amazonaws.com",
            [PurgeOutbox.VariableBase] = "ledger",
            [PurgeOutbox.VariableCertificatRacine] = "/app/rds-ca-global.pem",
            [PurgeOutbox.VariableRegion] = "ca-central-1",
            [PurgeOutbox.VariableRetentionHeures] = "48",
        };

        var configuration = PurgeOutbox.Lire(variables.GetValueOrDefault);

        var chaine = new NpgsqlConnectionStringBuilder(configuration.Chaine);
        Assert.Equal(("ledger_maintenance", SslMode.VerifyFull, null), (chaine.Username, chaine.SslMode, chaine.Password));
        Assert.True(configuration.Authentification.AuthentificationIam);
        Assert.Equal("ca-central-1", configuration.Authentification.Region);
        Assert.Equal(TimeSpan.FromHours(48), configuration.Retention);
        Assert.Equal("ledger_outbox", configuration.Slot);
    }

    [Fact]
    public void Lire_variables_manquantes_sont_signalees()
    {
        var configuration = PurgeOutbox.Lire(_ => null);

        Assert.Null(configuration.Chaine);
        Assert.Equal(
            [PurgeOutbox.VariableHote, PurgeOutbox.VariableBase, PurgeOutbox.VariableCertificatRacine, PurgeOutbox.VariableRegion],
            configuration.VariablesManquantes);
        Assert.Equal(TimeSpan.FromDays(7), configuration.Retention);
    }

    [Fact]
    public void Lire_chaine_complete_en_local_sans_iam()
    {
        var configuration = PurgeOutbox.Lire(v => v == PurgeOutbox.VariableComplete ? "Host=localhost;Username=ledger_maintenance" : null);

        Assert.Equal("Host=localhost;Username=ledger_maintenance", configuration.Chaine);
        Assert.False(configuration.Authentification.AuthentificationIam);
    }
}

[Collection(PostgresGroup.Name)]
[Trait("Category", "Integration")]
public sealed class CommandePurgeTests(PostgresFixture database)
{
    [Fact]
    public async Task ExecuterAsync_appelle_la_fonction_de_purge_avec_le_role_de_maintenance()
    {
        var slot = "commande_" + Guid.NewGuid().ToString("N");
        await using (var proprietaire = NpgsqlDataSource.Create(database.OwnerConnectionString))
        await using (var creation = proprietaire.CreateCommand($"SELECT pg_create_logical_replication_slot('{slot}', 'pgoutput')"))
        {
            await creation.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        try
        {
            await using var source = NpgsqlDataSource.Create(database.ChaineMaintenance);
            var supprimees = await PurgeOutbox.ExecuterAsync(source, TimeSpan.FromDays(3650), slot, TestContext.Current.CancellationToken);

            Assert.True(supprimees >= 0);
        }
        finally
        {
            await using var proprietaire = NpgsqlDataSource.Create(database.OwnerConnectionString);
            await using var suppression = proprietaire.CreateCommand($"SELECT pg_drop_replication_slot('{slot}')");
            await suppression.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }
}
