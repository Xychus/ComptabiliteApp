using System.Text.Json;
using System.Text.Json.Nodes;
using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application.Balances;
using Ledger.Projections.Application.Events;
using Ledger.Projections.Application.Periods;
using Ledger.Projections.Application.Processing;
using Ledger.Projections.Application.Snapshots;
using Ledger.Projections.Application.Upcasting;
using Ledger.Projections.Tests.Support;

namespace Ledger.Projections.Application.Tests.Events;

/// <summary>Décodage des enveloppes : contrats V1, upcasting à la lecture, erreurs bruyantes.</summary>
public sealed class EventDecoderTests
{
    private const string Renamed = "TestRenamed";

    private readonly JournalBuilder _journal = new("ACME");

    [Fact]
    public async Task Une_enveloppe_V1_est_presentee_a_la_projection_qui_traite_son_type()
    {
        var envelope = _journal.Opened("2026-03");
        var probe = new Probe<PeriodOpenedV1>();

        var decoded = EventDecoder.Default.Decode(envelope);
        await decoded.DispatchToAsync(probe, TestContext.Current.CancellationToken);

        Assert.True(decoded.IsHandledBy(probe));
        var received = Assert.Single(probe.Received);
        Assert.Equal(new PeriodOpenedV1("ACME", "2026-03"), received.Data);
        Assert.Equal(EventContext.From(envelope), received.Context);
    }

    [Fact]
    public async Task Une_projection_qui_ne_traite_pas_le_type_n_est_pas_appelee()
    {
        var probe = new Probe<PeriodOpenedV1>();

        var decoded = EventDecoder.Default.Decode(_journal.Posted(new DateOnly(2026, 3, 2), JournalBuilder.Debit("1000", 1m), JournalBuilder.Credit("4000", 1m)));
        await decoded.DispatchToAsync(probe, TestContext.Current.CancellationToken);

        Assert.False(decoded.IsHandledBy(probe));
        Assert.Empty(probe.Received);
    }

    [Fact]
    public void Un_type_inconnu_est_une_erreur() =>
        Assert.Throws<EventDecodingException>(() => EventDecoder.Default.Decode(_journal.Opened("2026-03") with { EventType = "Inconnu" }));

    [Fact]
    public void Une_version_plus_recente_que_le_worker_est_une_erreur()
    {
        var error = Assert.Throws<EventDecodingException>(() => EventDecoder.Default.Decode(_journal.Opened("2026-03") with { SchemaVersion = 2 }));

        Assert.Contains("plus récent", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Une_charge_invalide_est_une_erreur()
    {
        var envelope = _journal.Opened("2026-03") with { Payload = JsonSerializer.SerializeToElement(new { entityId = 12 }) };

        var error = Assert.Throws<EventDecodingException>(() => EventDecoder.Default.Decode(envelope));

        Assert.IsType<JsonException>(error.InnerException);
    }

    [Fact]
    public void Une_charge_nulle_est_une_erreur()
    {
        var envelope = _journal.Opened("2026-03") with { Payload = JsonSerializer.SerializeToElement<object?>(null) };

        Assert.Throws<EventDecodingException>(() => EventDecoder.Default.Decode(envelope));
    }

    [Fact]
    public async Task Une_charge_ancienne_est_convertie_par_la_chaine_d_upcasters()
    {
        var decoder = new EventDecoder.Builder()
            .Add<RenamedV3>(Renamed, 3)
            .Build(new EventUpcasterChain([new AddLabel(), new RenameName()]));
        var envelope = _journal.Opened("2026-03") with
        {
            EventType = Renamed,
            SchemaVersion = 1,
            Payload = JsonSerializer.SerializeToElement(new { name = "Caisse" }),
        };
        var probe = new Probe<RenamedV3>();

        await decoder.Decode(envelope).DispatchToAsync(probe, TestContext.Current.CancellationToken);

        Assert.Equal(new RenamedV3("Caisse", "défaut"), Assert.Single(probe.Received).Data);
    }

    [Fact]
    public void Un_maillon_manquant_de_la_chaine_est_une_erreur()
    {
        var decoder = new EventDecoder.Builder().Add<RenamedV3>(Renamed, 3).Build(new EventUpcasterChain([new AddLabel()]));
        var envelope = _journal.Opened("2026-03") with { EventType = Renamed, SchemaVersion = 1, Payload = JsonSerializer.SerializeToElement(new { name = "x" }) };

        var error = Assert.Throws<EventDecodingException>(() => decoder.Decode(envelope));

        Assert.Contains("v2 vers v3", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Deux_upcasters_pour_la_meme_version_sont_refuses() =>
        Assert.Throws<ArgumentException>(() => new EventUpcasterChain([new AddLabel(), new AddLabel()]));

    [Fact]
    public void Un_type_ne_peut_etre_enregistre_qu_une_fois() =>
        Assert.Throws<ArgumentException>(() => EventDecoder.CreateBuilder().Add<PeriodOpenedV1>(EventTypes.PeriodOpened, 1));

    [Fact]
    public void Les_types_traites_par_une_projection_sont_ceux_de_ses_gestionnaires()
    {
        Assert.Equal(
            [EventTypes.JournalEntryPosted, EventTypes.JournalEntryReversed],
            EventDecoder.Default.EventTypesHandledBy(new AccountBalancesProjection(null!)));
        Assert.Equal(
            [EventTypes.PeriodOpened, EventTypes.PeriodSealed],
            EventDecoder.Default.EventTypesHandledBy(new PeriodsProjection(null!)));
        Assert.Equal(
            [EventTypes.PeriodSealed],
            EventDecoder.Default.EventTypesHandledBy(new PeriodSnapshotsProjection(null!, EventDecoder.Default, null!)));
    }

    private sealed record RenamedV3(string Label, string Category);

    /// <summary>v1 → v2 : ajoute <c>category</c>.</summary>
    private sealed class AddLabel : IEventUpcaster
    {
        public string EventType => Renamed;

        public int FromVersion => 1;

        public JsonElement Upcast(JsonElement payload)
        {
            var node = JsonNode.Parse(payload.GetRawText())!.AsObject();
            node["category"] = "défaut";
            return JsonSerializer.SerializeToElement(node);
        }
    }

    /// <summary>v2 → v3 : <c>name</c> devient <c>label</c>.</summary>
    private sealed class RenameName : IEventUpcaster
    {
        public string EventType => Renamed;

        public int FromVersion => 2;

        public JsonElement Upcast(JsonElement payload)
        {
            var node = JsonNode.Parse(payload.GetRawText())!.AsObject();
            node["label"] = node["name"]!.DeepClone();
            node.Remove("name");
            return JsonSerializer.SerializeToElement(node);
        }
    }

    private sealed class Probe<T> : IProjection, IEventHandler<T>
        where T : class
    {
        public List<ProjectedEvent<T>> Received { get; } = [];

        public string Name => "sonde";

        public Task HandleAsync(ProjectedEvent<T> projectedEvent, CancellationToken cancellationToken)
        {
            Received.Add(projectedEvent);
            return Task.CompletedTask;
        }
    }
}
