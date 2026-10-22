extern alias commande;
extern alias requete;

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Confluent.Kafka;
using Confluent.Kafka.Admin;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Ledger.Command.Infrastructure.Outbox;
using Ledger.Contracts.Events;
using Ledger.Contracts.Events.V1;
using Ledger.Migrations;
using Ledger.Projections.Worker;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;

namespace Ledger.EndToEnd.Tests.Support;

/// <summary>
/// Chaîne complète en conteneurs (ADR 0020) : PostgreSQL en réplication logique, Kafka, Kafka
/// Connect + Debezium avec la configuration réellement déployée, Keycloak avec le realm de test ;
/// les deux API et le worker de projections tournent dans le processus de test avec leur
/// composition réelle. Les mots de passe et secrets sont aléatoires, propres à l'exécution.
/// </summary>
public sealed class ChaineCdc : IAsyncLifetime
{
    public const string ImagePostgres = "postgres:18-alpine";
    public const string ImageKafka = "apache/kafka:4.3.2";
    public const string ImageDebezium = "quay.io/debezium/connect:3.7";
    public const string ImageKeycloak = "quay.io/keycloak/keycloak:26.8";

    public const string NomConnecteur = "ledger-outbox";
    public const string GroupeConsommateurs = "ledger-projections";
    public const string ClientEcriture = "ledger-local";
    public const string ClientLecture = "ledger-lecture-local";
    public const int PartitionsSujet = 3;

    /// <summary>Entité écrite avant l'enregistrement du connecteur : publiée par le snapshot initial.</summary>
    public const string EntiteAvantConnecteur = "E2E-AVANT-CONNECTEUR";
    public const string PeriodeAvantConnecteur = "2026-03";
    public const decimal MontantAvantConnecteur = 42.50m;

    private static readonly TimeSpan _delaiAttente = TimeSpan.FromSeconds(120);

    private readonly string _motDePasseApp = Guid.NewGuid().ToString("N");
    private readonly string _motDePasseProjection = Guid.NewGuid().ToString("N");
    private readonly string _motDePasseRequete = Guid.NewGuid().ToString("N");
    private readonly string _motDePasseDebezium = Guid.NewGuid().ToString("N");
    private readonly string _secretEcriture = Guid.NewGuid().ToString("N");
    private readonly string _secretLecture = Guid.NewGuid().ToString("N");

    private readonly INetwork _reseau;
    private readonly PostgreSqlContainer _postgres;
    private readonly KafkaContainer _kafka;
    private readonly IContainer _connect;
    private readonly IContainer _keycloak;
    private readonly HttpClient _http = new();

    private FabriqueApi<commande::Program>? _apiCommandes;
    private FabriqueApi<requete::Program>? _apiRequete;
    private IHost? _worker;

    public ChaineCdc()
    {
        _reseau = new NetworkBuilder().Build();
        _postgres = new PostgreSqlBuilder(ImagePostgres)
            .WithNetwork(_reseau)
            .WithNetworkAliases("postgres")
            .WithDatabase("ledger")
            .WithCommand("-c", "wal_level=logical")
            .Build();

        // KRaft et port d'hôte fixe : le courtier survit à l'arrêt et au redémarrage du conteneur (tests de panne).
        _kafka = new KafkaBuilder(ImageKafka)
            .WithKRaft()
            .WithNetwork(_reseau)
            .WithListener("kafka:19092")
            .WithPortBinding(PortLibre(), KafkaBuilder.KafkaPort)
            .Build();

        _connect = new ContainerBuilder(ImageDebezium)
            .WithNetwork(_reseau)
            .WithEnvironment("BOOTSTRAP_SERVERS", "kafka:19092")
            .WithEnvironment("GROUP_ID", "ledger-connect")
            .WithEnvironment("CONFIG_STORAGE_TOPIC", "_connect_configs")
            .WithEnvironment("OFFSET_STORAGE_TOPIC", "_connect_offsets")
            .WithEnvironment("STATUS_STORAGE_TOPIC", "_connect_status")
            .WithEnvironment("CONNECT_SCHEDULED_REBALANCE_MAX_DELAY_MS", "10000")
            .WithEnvironment("CONNECT_CONFIG_PROVIDERS", "env")
            .WithEnvironment("CONNECT_CONFIG_PROVIDERS_ENV_CLASS", "org.apache.kafka.common.config.provider.EnvVarConfigProvider")
            .WithEnvironment("LEDGER_DEBEZIUM_PASSWORD", _motDePasseDebezium)
            .WithPortBinding(8083, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(requete => requete.ForPort(8083).ForPath("/connectors")))
            .Build();

        _keycloak = new ContainerBuilder(ImageKeycloak)
            .WithNetwork(_reseau)
            .WithCommand("start-dev", "--import-realm")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_USERNAME", "admin")
            .WithEnvironment("KC_BOOTSTRAP_ADMIN_PASSWORD", Guid.NewGuid().ToString("N"))
            .WithEnvironment("KC_HEALTH_ENABLED", "true")
            .WithEnvironment("LEDGER_CLIENT_SECRET", _secretEcriture)
            .WithEnvironment("LEDGER_LECTURE_CLIENT_SECRET", _secretLecture)
            .WithResourceMapping(
                new FileInfo(Path.Join(AppContext.BaseDirectory, "deploy", "local", "keycloak", "ledger-realm.json")),
                new FileInfo("/opt/keycloak/data/import/ledger-realm.json"))
            .WithPortBinding(8080, true)
            .WithPortBinding(9000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(requete => requete.ForPort(9000).ForPath("/health/ready")))
            .Build();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public string ServeursKafka => _kafka.GetBootstrapAddress();

    private string UrlConnect => $"http://{_connect.Hostname}:{_connect.GetMappedPublicPort(8083)}";

    public string Autorite => $"http://{_keycloak.Hostname}:{_keycloak.GetMappedPublicPort(8080)}/realms/ledger";

    /// <summary>Rôle propriétaire : vérifications directes en base.</summary>
    public NpgsqlDataSource Proprietaire { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        await _reseau.CreateAsync().ConfigureAwait(false);
        await Task.WhenAll(_postgres.StartAsync(), _kafka.StartAsync(), _keycloak.StartAsync()).ConfigureAwait(false);
        await PreparerBaseAsync().ConfigureAwait(false);
        await CreerSujetAsync().ConfigureAwait(false);
        await _connect.StartAsync().ConfigureAwait(false);

        var connexion = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString());
        _apiCommandes = new FabriqueApi<commande::Program>(new Dictionary<string, string?>
        {
            ["Ledger:Authentication:Authority"] = Autorite,
            ["ConnectionStrings:Ledger"] = ChaineRole(connexion, "ledger_app", _motDePasseApp),
        });
        _apiRequete = new FabriqueApi<requete::Program>(new Dictionary<string, string?>
        {
            ["Ledger:Authentication:Authority"] = Autorite,
            ["ConnectionStrings:Ledger"] = ChaineRole(connexion, "ledger_query", _motDePasseRequete),
        });

        // Écrit avant que le connecteur existe : seul le snapshot initial peut le publier.
        using (var client = await NouveauClientAsync().ConfigureAwait(false))
        {
            await client.PreparerEntiteAsync(EntiteAvantConnecteur, PeriodeAvantConnecteur).ConfigureAwait(false);
            await client.PasserEcritureAsync(EntiteAvantConnecteur, new DateOnly(2026, 3, 5), "1000", "4000", MontantAvantConnecteur).ConfigureAwait(false);
        }

        await EnregistrerConnecteurAsync().ConfigureAwait(false);
        await DemarrerWorkerAsync().ConfigureAwait(false);
    }

    public async Task<ClientGrandLivre> NouveauClientAsync(string client = ClientEcriture)
    {
        var jeton = await JetonAsync(client).ConfigureAwait(false);
        return ClientGrandLivre.Creer(_apiCommandes!.CreateClient(), _apiRequete!.CreateClient(), jeton);
    }

    /// <summary>Jeton <c>client_credentials</c> émis par le realm de test.</summary>
    public async Task<string> JetonAsync(string client)
    {
        var secret = client == ClientLecture ? _secretLecture : _secretEcriture;
        using var formulaire = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = client,
            ["client_secret"] = secret,
        });
        using var reponse = await _http.PostAsync(new Uri($"{Autorite}/protocol/openid-connect/token"), formulaire, Ct).ConfigureAwait(false);
        reponse.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await reponse.Content.ReadAsStringAsync(Ct).ConfigureAwait(false));
        return document.RootElement.GetProperty("access_token").GetString()!;
    }

    public async Task DemarrerWorkerAsync()
    {
        var connexion = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString());
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Development", Args = [] });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:" + ProjectionWorkerHosting.ConnectionStringName] = ChaineRole(connexion, "ledger_projection", _motDePasseProjection),
            ["Ledger:Kafka:BootstrapServers"] = ServeursKafka,
            ["Ledger:Kafka:SecurityProtocol"] = "Plaintext",
            ["Ledger:Kafka:ConsumerGroup"] = GroupeConsommateurs,
        });
        builder.AddProjectionWorker(consume: true);
        builder.Logging.ClearProviders();
        _worker = builder.Build();
        await _worker.StartAsync(Ct).ConfigureAwait(false);
    }

    /// <summary>Arrêt du worker (comme un arrêt de tâche ECS) : la transaction en cours est validée ou annulée.</summary>
    public async Task ArreterWorkerAsync()
    {
        if (_worker is null)
        {
            return;
        }

        await _worker.StopAsync(Ct).ConfigureAwait(false);
        _worker.Dispose();
        _worker = null;
    }

    public Task ArreterKafkaAsync() => _kafka.StopAsync(Ct);

    public Task DemarrerKafkaAsync() => _kafka.StartAsync(Ct);

    /// <summary>
    /// Attend, au plus <see cref="_delaiAttente"/>, que <paramref name="condition"/> soit vraie.
    /// Échoue tout de suite si le worker s'est arrêté de lui-même (erreur de projection).
    /// </summary>
    public async Task AttendreAsync(Func<Task<bool>> condition, string description)
    {
        ArgumentNullException.ThrowIfNull(condition);
        var echeance = DateTimeOffset.UtcNow + _delaiAttente;
        while (!await condition().ConfigureAwait(false))
        {
            if (_worker?.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested == true)
            {
                Assert.Fail($"Le worker de projections s'est arrêté en attendant : {description}.");
            }

            if (DateTimeOffset.UtcNow > echeance)
            {
                Assert.Fail($"Délai dépassé ({_delaiAttente.TotalSeconds} s) : {description}.{Environment.NewLine}{await DiagnosticAsync().ConfigureAwait(false)}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), Ct).ConfigureAwait(false);
        }
    }

    /// <summary>Statut du connecteur et derniers avertissements de Kafka Connect, joints aux échecs d'attente.</summary>
    public async Task<string> DiagnosticAsync()
    {
        string statut;
        try
        {
            statut = await _http.GetStringAsync(new Uri($"{UrlConnect}/connectors/{NomConnecteur}/status")).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            statut = exception.Message;
        }

        var (sortie, erreurs) = await _connect.GetLogsAsync(ct: CancellationToken.None).ConfigureAwait(false);
        var avertissements = (sortie + erreurs).Split('\n')
            .Where(ligne => ligne.Contains(" WARN ", StringComparison.Ordinal) || ligne.Contains(" ERROR ", StringComparison.Ordinal))
            .TakeLast(30);
        return $"Connecteur : {statut}{Environment.NewLine}{string.Join(Environment.NewLine, avertissements)}";
    }

    /// <summary>
    /// Attente bornée sur l'indicateur de fraîcheur : l'API de requête a vu la séquence
    /// <paramref name="sequence"/> et l'encaisse de l'entité vaut <paramref name="encaisse"/>.
    /// </summary>
    public Task AttendreEncaisseAsync(ClientGrandLivre client, string entite, long sequence, decimal encaisse) =>
        AttendreAsync(
            async () =>
            {
                var (solde, vuJusqua) = await client.LireEncaisseAsync(entite).ConfigureAwait(false);
                return vuJusqua >= sequence && solde == encaisse;
            },
            $"encaisse de {entite} = {encaisse} jusqu'à la séquence {sequence}");

    /// <summary>Nombre de messages du sujet (somme des offsets de fin de chaque partition).</summary>
    public long MessagesPublies()
    {
        using var consommateur = new ConsumerBuilder<Ignore, Ignore>(new ConsumerConfig { BootstrapServers = ServeursKafka, GroupId = "e2e-mesure-" + Guid.NewGuid().ToString("N") }).Build();
        return Enumerable.Range(0, PartitionsSujet)
            .Sum(partition => consommateur.QueryWatermarkOffsets(new TopicPartition(OutboxMessages.Topic, partition), TimeSpan.FromSeconds(10)).High.Value);
    }

    /// <summary>Messages lus par le worker, d'après les checkpoints en base (ADR 0015).</summary>
    public async Task<long> MessagesConsommesAsync()
    {
        await using var commande = Proprietaire.CreateCommand(
            "SELECT COALESCE(SUM(next_offset), 0)::BIGINT FROM projections.consumer_offsets WHERE consumer_group = @groupe AND topic = @sujet");
        commande.Parameters.AddWithValue("groupe", GroupeConsommateurs);
        commande.Parameters.AddWithValue("sujet", OutboxMessages.Topic);
        return (long)(await commande.ExecuteScalarAsync(Ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Attend que chaque événement du journal soit publié, puis que tout le sujet soit consommé.
    /// La comparaison porte sur les identifiants : la livraison « au moins une fois » de Connect
    /// peut republier des messages, un simple compte serait atteint trop tôt.
    /// </summary>
    public async Task AttendreConsommationCompleteAsync()
    {
        var journal = new List<Guid>();
        await using (var commande = Proprietaire.CreateCommand("SELECT event_id FROM ledger.events"))
        await using (var lecteur = await commande.ExecuteReaderAsync(Ct).ConfigureAwait(false))
        {
            while (await lecteur.ReadAsync(Ct).ConfigureAwait(false))
            {
                journal.Add(lecteur.GetGuid(0));
            }
        }

        await AttendreAsync(
            () => Task.FromResult(journal.Except(LireSujet().Select(m => m.Enveloppe.EventId)).Any() is false),
            $"{journal.Count} événements du journal publiés").ConfigureAwait(false);
        var publies = MessagesPublies();
        await AttendreAsync(async () => await MessagesConsommesAsync().ConfigureAwait(false) >= publies, $"{publies} messages consommés").ConfigureAwait(false);
    }

    /// <summary>Lit le sujet entier, partition par partition, dans l'ordre des offsets.</summary>
    public IReadOnlyList<(int Partition, long Offset, EventEnvelopeV1 Enveloppe)> LireSujet()
    {
        using var consommateur = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = ServeursKafka,
            GroupId = "e2e-lecture-" + Guid.NewGuid().ToString("N"),
            EnableAutoCommit = false,
        }).Build();
        var messages = new List<(int, long, EventEnvelopeV1)>();
        for (var partition = 0; partition < PartitionsSujet; partition++)
        {
            var sujetPartition = new TopicPartition(OutboxMessages.Topic, partition);
            var fin = consommateur.QueryWatermarkOffsets(sujetPartition, TimeSpan.FromSeconds(10)).High.Value;
            consommateur.Assign(new TopicPartitionOffset(sujetPartition, Offset.Beginning));
            while (messages.Count(m => m.Item1 == partition) < fin)
            {
                var resultat = consommateur.Consume(TimeSpan.FromSeconds(10)) ?? throw new TimeoutException($"Partition {partition} : lecture interrompue.");
                messages.Add((partition, resultat.Offset.Value, JsonSerializer.Deserialize<EventEnvelopeV1>(resultat.Message.Value, EventJson.Options)!));
            }
        }

        return messages;
    }

    public async Task<long> CompterAsync(string sql, params (string Nom, object Valeur)[] parametres)
    {
        await using var commande = Proprietaire.CreateCommand(sql);
        foreach (var (nom, valeur) in parametres)
        {
            commande.Parameters.AddWithValue(nom, valeur);
        }

        return (long)(await commande.ExecuteScalarAsync(Ct).ConfigureAwait(false))!;
    }

    public async ValueTask DisposeAsync()
    {
        await ArreterWorkerAsync().ConfigureAwait(false);
        if (_apiCommandes is not null)
        {
            await _apiCommandes.DisposeAsync().ConfigureAwait(false);
        }

        if (_apiRequete is not null)
        {
            await _apiRequete.DisposeAsync().ConfigureAwait(false);
        }

        if (Proprietaire is not null)
        {
            await Proprietaire.DisposeAsync().ConfigureAwait(false);
        }

        _http.Dispose();
        await _connect.DisposeAsync().ConfigureAwait(false);
        await _keycloak.DisposeAsync().ConfigureAwait(false);
        await _kafka.DisposeAsync().ConfigureAwait(false);
        await _postgres.DisposeAsync().ConfigureAwait(false);
        await _reseau.DisposeAsync().ConfigureAwait(false);
    }

    private static string ChaineRole(NpgsqlConnectionStringBuilder proprietaire, string role, string motDePasse) =>
        new NpgsqlConnectionStringBuilder(proprietaire.ConnectionString) { Username = role, Password = motDePasse }.ConnectionString;

    private static int PortLibre()
    {
        using var ecoute = new TcpListener(IPAddress.Loopback, 0);
        ecoute.Start();
        return ((IPEndPoint)ecoute.LocalEndpoint).Port;
    }

    /// <summary>Migrations, puis connexion des rôles (en AWS : Secrets Manager, phase 7).</summary>
    private async Task PreparerBaseAsync()
    {
        var migration = LedgerMigrator.Upgrade(_postgres.GetConnectionString(), logToConsole: false);
        if (!migration.Successful)
        {
            throw new InvalidOperationException("Échec des migrations.", migration.Error);
        }

        Proprietaire = NpgsqlDataSource.Create(_postgres.GetConnectionString());
        foreach (var (role, motDePasse) in new[] { ("ledger_app", _motDePasseApp), ("ledger_projection", _motDePasseProjection), ("ledger_query", _motDePasseRequete), ("debezium", _motDePasseDebezium) })
        {
            await using var commande = Proprietaire.CreateCommand($"ALTER ROLE {role} LOGIN PASSWORD '{motDePasse}'");
            await commande.ExecuteNonQueryAsync().ConfigureAwait(false);
        }
    }

    private async Task CreerSujetAsync()
    {
        using var admin = new AdminClientBuilder(new AdminClientConfig { BootstrapServers = ServeursKafka }).Build();
        await admin.CreateTopicsAsync([new TopicSpecification { Name = OutboxMessages.Topic, NumPartitions = PartitionsSujet, ReplicationFactor = 1 }]).ConfigureAwait(false);
    }

    /// <summary>Fichier invariant + paramètres locaux (mot de passe en référence <c>${env:...}</c>), comme en compose.</summary>
    private async Task EnregistrerConnecteurAsync()
    {
        var configuration = ConfigurationConnecteur.Fusionner(
            ConfigurationConnecteur.Lire(ConfigurationConnecteur.FichierInvariant),
            ConfigurationConnecteur.Lire(ConfigurationConnecteur.FichierLocal));
        var urlConnect = UrlConnect;
        using (var reponse = await _http.PutAsJsonAsync(new Uri($"{urlConnect}/connectors/{NomConnecteur}/config"), configuration).ConfigureAwait(false))
        {
            Assert.True(reponse.IsSuccessStatusCode, $"Enregistrement du connecteur : {(int)reponse.StatusCode} {await reponse.Content.ReadAsStringAsync().ConfigureAwait(false)}");
        }

        var echeance = DateTimeOffset.UtcNow + _delaiAttente;
        while (true)
        {
            // 404 tant que le connecteur n'est pas encore créé par le groupe Kafka Connect.
            using var reponse = await _http.GetAsync(new Uri($"{urlConnect}/connectors/{NomConnecteur}/status")).ConfigureAwait(false);
            var statut = await reponse.Content.ReadAsStringAsync().ConfigureAwait(false);
            List<string?> taches = [];
            if (reponse.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(statut);
                taches = [.. document.RootElement.GetProperty("tasks").EnumerateArray().Select(t => t.GetProperty("state").GetString())];
            }

            if (taches.Count > 0 && taches.All(etat => etat == "RUNNING"))
            {
                return;
            }

            if (taches.Contains("FAILED") || DateTimeOffset.UtcNow > echeance)
            {
                throw new InvalidOperationException($"Connecteur non démarré : {statut}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }
    }
}

[CollectionDefinition(Nom)]
public sealed class GroupeChaineCdc : ICollectionFixture<ChaineCdc>
{
    public const string Nom = "chaine-cdc";
}
