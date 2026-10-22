using Ledger.Command.Infrastructure.Outbox;
using Ledger.EndToEnd.Tests.Support;

namespace Ledger.EndToEnd.Tests;

/// <summary>
/// Le fichier <c>deploy/debezium/ledger-outbox.json</c> est réutilisé tel quel sur MSK Connect
/// (phase 7) : il ne contient rien qui dépende de l'environnement (ADR 0020). Sans Docker.
/// </summary>
public sealed class ConfigurationConnecteurTests
{
    private static readonly IReadOnlyDictionary<string, string> _invariant = ConfigurationConnecteur.Lire(ConfigurationConnecteur.FichierInvariant);

    [Fact]
    public void La_configuration_invariante_ne_contient_aucun_parametre_d_environnement()
    {
        Assert.Empty(_invariant.Keys.Intersect(ConfigurationConnecteur.ClesEnvironnement));
        Assert.DoesNotContain(_invariant.Values, valeur => valeur.Contains("${env:", StringComparison.Ordinal));
    }

    [Fact]
    public void Les_parametres_locaux_ne_contiennent_que_des_parametres_d_environnement()
    {
        var local = ConfigurationConnecteur.Lire(ConfigurationConnecteur.FichierLocal);

        Assert.All(local.Keys, cle => Assert.Contains(cle, ConfigurationConnecteur.ClesEnvironnement));
        Assert.Equal("${env:LEDGER_DEBEZIUM_PASSWORD}", local["database.password"]);
    }

    [Fact]
    public void Le_routeur_d_outbox_lit_les_colonnes_ecrites_par_l_API_de_commandes()
    {
        Assert.Equal("io.debezium.transforms.outbox.EventRouter", _invariant["transforms.outbox.type"]);
        Assert.Equal("ledger.outbox", _invariant["table.include.list"]);
        Assert.Equal("event_id", _invariant["transforms.outbox.table.field.event.id"]);
        Assert.Equal("partition_key", _invariant["transforms.outbox.table.field.event.key"]);
        Assert.Equal("payload", _invariant["transforms.outbox.table.field.event.payload"]);
        Assert.Equal("topic", _invariant["transforms.outbox.route.by.field"]);
        Assert.Equal("${routedByValue}", _invariant["transforms.outbox.route.topic.replacement"]);
        Assert.Equal("ledger.events", OutboxMessages.Topic);
    }

    [Fact]
    public void Le_connecteur_ne_perd_ni_ne_saute_aucun_message()
    {
        // Snapshot initial (lignes écrites avant le connecteur), aucune tolérance d'erreur,
        // producteur qui n'abandonne jamais un envoi, publication et slot créés par les migrations.
        Assert.Equal("initial", _invariant["snapshot.mode"]);
        Assert.Equal("none", _invariant["errors.tolerance"]);
        Assert.Equal(int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), _invariant["producer.override.delivery.timeout.ms"]);
        Assert.Equal("disabled", _invariant["publication.autocreate.mode"]);
        Assert.Equal("ledger_pub", _invariant["publication.name"]);
        Assert.Equal("ledger_outbox", _invariant["slot.name"]);
    }

    [Fact]
    public void Un_parametre_ne_peut_pas_redefinir_une_cle_invariante()
    {
        var parametres = new Dictionary<string, string> { ["database.hostname"] = "h", ["snapshot.mode"] = "no_data" };

        var erreur = Assert.Throws<InvalidOperationException>(() => ConfigurationConnecteur.Fusionner(_invariant, parametres));

        Assert.Contains("snapshot.mode", erreur.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void La_fusion_ajoute_les_parametres_a_la_configuration_invariante()
    {
        var fusion = ConfigurationConnecteur.Fusionner(_invariant, new Dictionary<string, string> { ["database.hostname"] = "aurora" });

        Assert.Equal(_invariant.Count + 1, fusion.Count);
        Assert.Equal("aurora", fusion["database.hostname"]);
    }
}
