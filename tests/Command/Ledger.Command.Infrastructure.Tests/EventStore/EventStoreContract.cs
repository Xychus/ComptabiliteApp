using System.Text.Json;
using Ledger.Command.Application.EventStore;
using Ledger.Command.Infrastructure.Outbox;
using Ledger.Contracts.Events;
using Ledger.Contracts.Events.V1;
using Ledger.Contracts.Integrity;
using Ledger.SharedKernel;

namespace Ledger.Command.Infrastructure.Tests.EventStore;

/// <summary>
/// Suite de contrat de l'event store (Liskov) : toute implémentation de <see cref="IEventReader"/>,
/// <see cref="IEventAppender"/> et <see cref="IUnitOfWork"/> doit la passer.
/// Chaque test travaille sur une entité et des flux qui lui sont propres.
/// </summary>
public abstract class EventStoreContract
{
    private readonly string _entity = "E" + Guid.CreateVersion7().ToString("N");

    protected abstract IEventStoreUnderTest Store { get; }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Un_flux_inexistant_est_vide() =>
        Assert.Empty(await Store.Reader.ReadStreamAsync(Stream("inexistant"), Ct));

    [Fact]
    public async Task Une_validation_sans_evenement_reussit_sans_rien_ecrire()
    {
        var result = await Store.BeginUnitOfWork().CommitAsync();

        Assert.True(result.IsSuccess);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task L_evenement_relu_restitue_tous_les_champs_fournis()
    {
        var metadata = new EventMetadata(Guid.CreateVersion7(), Guid.CreateVersion7(), "caisse-12");
        var written = Event(Stream("a"), 1) with
        {
            BusinessDate = new DateOnly(2026, 3, 31),
            BalanceAfter = 1234.50m,
            Metadata = metadata,
            Payload = Json("""{"texte":"Été — « 5 $ »","montant":"1234.50","lignes":[{"n":1},{"n":2}],"vide":null}"""),
        };

        await CommitOk(written);
        var read = Assert.Single(await Store.Reader.ReadStreamAsync(written.StreamId, Ct));

        Assert.Equal(written.EventId, read.EventId);
        Assert.Equal(written.StreamId, read.StreamId);
        Assert.Equal(1, read.StreamVersion);
        Assert.Equal(written.EventType, read.EventType);
        Assert.Equal(written.SchemaVersion, read.SchemaVersion);
        Assert.Equal(_entity, read.EntityId);
        Assert.Equal(written.BusinessDate, read.BusinessDate);
        Assert.Equal("2026-03", read.Period);
        Assert.Equal(1234.50m, read.BalanceAfter);
        Assert.Equal(metadata, read.Metadata);
        Assert.Equal(JsonCanonicalizer.Canonicalize(written.Payload), JsonCanonicalizer.Canonicalize(read.Payload));
    }

    [Fact]
    public async Task Les_champs_facultatifs_peuvent_etre_absents()
    {
        var written = Event(Stream("a"), 1) with { BusinessDate = null, BalanceAfter = null, Metadata = new EventMetadata(Guid.CreateVersion7(), null, "admin") };

        await CommitOk(written);
        var read = Assert.Single(await Store.Reader.ReadStreamAsync(written.StreamId, Ct));

        Assert.Null(read.BusinessDate);
        Assert.Null(read.BalanceAfter);
        Assert.Null(read.Metadata.CausationId);
    }

    [Fact]
    public async Task L_horodatage_est_celui_du_serveur_tronque_a_la_microseconde()
    {
        var stored = await CommitOk(Event(Stream("a"), 1));

        var expected = EventHash.TruncateToMicroseconds(Store.Clock.UtcNow);
        Assert.Equal(expected, stored[0].RecordedAt);
        Assert.Equal(expected, (await Store.Reader.ReadStreamAsync(Stream("a"), Ct))[0].RecordedAt);
    }

    [Fact]
    public async Task La_sequence_globale_croit_dans_l_ordre_d_ecriture()
    {
        var first = await CommitOk(Event(Stream("a"), 1), Event(Stream("b"), 1), Event(Stream("a"), 2));
        var second = await CommitOk(Event(Stream("c"), 1));

        var sequences = first.Concat(second).Select(e => e.GlobalSequence).ToList();
        Assert.All(sequences, sequence => Assert.True(sequence > 0));
        Assert.Equal(sequences.Order(), sequences);
        Assert.Equal(sequences.Count, sequences.Distinct().Count());
    }

    [Fact]
    public async Task Un_flux_se_relit_par_version_croissante()
    {
        await CommitOk(Event(Stream("a"), 1), Event(Stream("autre"), 1));
        await CommitOk(Event(Stream("a"), 2), Event(Stream("a"), 3));

        var read = await Store.Reader.ReadStreamAsync(Stream("a"), Ct);

        Assert.Equal([1L, 2L, 3L], read.Select(e => e.StreamVersion));
        Assert.All(read, e => Assert.Equal(Stream("a"), e.StreamId));
    }

    [Fact]
    public async Task Une_version_deja_prise_est_un_conflit()
    {
        await CommitOk(Event(Stream("a"), 1));

        var result = await Store.BeginUnitOfWork().CommitAsync(Event(Stream("a"), 1));

        AssertRejected(result, EventStoreErrors.ConcurrencyConflictCode);
        Assert.Single(await Store.Reader.ReadStreamAsync(Stream("a"), Ct));
    }

    [Theory]
    [InlineData(new long[] { 2 })]
    [InlineData(new long[] { 1, 3 })]
    [InlineData(new long[] { 1, 1 })]
    public async Task Une_version_qui_ne_suit_pas_la_tete_du_flux_est_un_conflit(long[] versions)
    {
        var result = await Store.BeginUnitOfWork().CommitAsync([.. versions.Select(v => Event(Stream("a"), v))]);

        AssertRejected(result, EventStoreErrors.ConcurrencyConflictCode);
        Assert.Empty(await Store.Reader.ReadStreamAsync(Stream("a"), Ct));
    }

    [Fact]
    public async Task Deux_ecritures_concurrentes_de_la_meme_version_un_seul_gagnant()
    {
        for (var round = 0; round < 20; round++)
        {
            var stream = Stream($"course-{round}");
            var results = await Task.WhenAll(
                Task.Run(() => Store.BeginUnitOfWork().CommitAsync(Event(stream, 1)), Ct),
                Task.Run(() => Store.BeginUnitOfWork().CommitAsync(Event(stream, 1)), Ct));

            Assert.Single(results, r => r.IsSuccess);
            AssertRejected(Assert.Single(results, r => r.IsFailure), EventStoreErrors.ConcurrencyConflictCode);
            Assert.Single(await Store.Reader.ReadStreamAsync(stream, Ct));
        }
    }

    [Fact]
    public async Task Un_identifiant_d_evenement_ne_s_ecrit_qu_une_fois()
    {
        var first = Event(Stream("a"), 1);
        await CommitOk(first);

        var result = await Store.BeginUnitOfWork().CommitAsync(Event(Stream("b"), 1) with { EventId = first.EventId });

        AssertRejected(result, EventStoreErrors.ConcurrencyConflictCode);
        Assert.Empty(await Store.Reader.ReadStreamAsync(Stream("b"), Ct));
    }

    [Fact]
    public async Task Un_identifiant_d_ecriture_n_est_reserve_qu_une_fois_dans_tout_le_journal()
    {
        var entryId = Guid.CreateVersion7();
        await CommitOk(Event(Stream("origine"), 1) with { ClaimedEntryId = entryId });

        var result = await Store.BeginUnitOfWork().CommitAsync(Event(Stream("autre"), 1) with { ClaimedEntryId = entryId });

        AssertRejected(result, EventStoreErrors.EntryIdAlreadyUsedCode);
        Assert.Empty(await Store.Reader.ReadStreamAsync(Stream("autre"), Ct));
    }

    [Fact]
    public async Task Un_identifiant_d_ecriture_reserve_deux_fois_dans_un_lot_est_refuse()
    {
        var entryId = Guid.CreateVersion7();

        var result = await Store.BeginUnitOfWork().CommitAsync(
            Event(Stream("a"), 1) with { ClaimedEntryId = entryId },
            Event(Stream("b"), 1) with { ClaimedEntryId = entryId });

        AssertRejected(result, EventStoreErrors.EntryIdAlreadyUsedCode);
        Assert.Empty(await Store.Reader.ReadStreamAsync(Stream("a"), Ct));
    }

    [Fact]
    public async Task Le_hash_chaine_les_versions_d_un_flux_et_se_recalcule_a_la_relecture()
    {
        await CommitOk(Event(Stream("a"), 1), Event(Stream("a"), 2));
        await CommitOk(Event(Stream("a"), 3));

        var read = await Store.Reader.ReadStreamAsync(Stream("a"), Ct);

        Assert.Null(read[0].PreviousHash);
        Assert.Equal(read[0].Hash, read[1].PreviousHash);
        Assert.Equal(read[1].Hash, read[2].PreviousHash);
        Assert.All(read, e => Assert.Equal(EventHash.Compute(e.ToHashInput()), e.Hash));
        Assert.All(read, e => Assert.Matches("^[0-9a-f]{64}$", e.Hash));
    }

    [Fact]
    public async Task Chaque_flux_a_sa_propre_chaine()
    {
        await CommitOk(Event(Stream("a"), 1));

        var other = await CommitOk(Event(Stream("b"), 1));

        Assert.Null(other[0].PreviousHash);
    }

    [Fact]
    public async Task Les_evenements_renvoyes_a_la_validation_sont_ceux_relus()
    {
        var stored = await CommitOk(Event(Stream("a"), 1) with { BalanceAfter = 5m });

        var read = Assert.Single(await Store.Reader.ReadStreamAsync(Stream("a"), Ct));

        Assert.Equal(stored[0].GlobalSequence, read.GlobalSequence);
        Assert.Equal(stored[0].Hash, read.Hash);
        Assert.Equal(stored[0].RecordedAt, read.RecordedAt);
    }

    [Fact]
    public async Task Un_echec_n_ecrit_rien_ni_evenement_ni_outbox()
    {
        await CommitOk(Event(Stream("occupe"), 1));
        var fresh = Event(Stream("libre"), 1);

        var result = await Store.BeginUnitOfWork().CommitAsync(fresh, Event(Stream("occupe"), 1));

        AssertRejected(result, EventStoreErrors.ConcurrencyConflictCode);
        Assert.Empty(await Store.Reader.ReadStreamAsync(Stream("libre"), Ct));
        Assert.Empty(await Store.ReadOutboxAsync([fresh.EventId]));
    }

    [Fact]
    public async Task Un_echec_sur_identifiant_reserve_n_ecrit_rien()
    {
        var entryId = Guid.CreateVersion7();
        await CommitOk(Event(Stream("origine"), 1) with { ClaimedEntryId = entryId });
        var fresh = Event(Stream("libre"), 1);

        var result = await Store.BeginUnitOfWork().CommitAsync(fresh, Event(Stream("autre"), 1) with { ClaimedEntryId = entryId });

        AssertRejected(result, EventStoreErrors.EntryIdAlreadyUsedCode);
        Assert.Empty(await Store.Reader.ReadStreamAsync(Stream("libre"), Ct));
        Assert.Empty(await Store.ReadOutboxAsync([fresh.EventId]));
    }

    [Fact]
    public async Task Les_evenements_prepares_sont_abandonnes_apres_un_echec()
    {
        await CommitOk(Event(Stream("occupe"), 1));
        var unitOfWork = Store.BeginUnitOfWork();
        AssertRejected(await unitOfWork.CommitAsync(Event(Stream("occupe"), 1)), EventStoreErrors.ConcurrencyConflictCode);

        var retry = await unitOfWork.CommitAsync(Event(Stream("occupe"), 2));

        Assert.True(retry.IsSuccess, retry.IsFailure ? retry.Error.Message : null);
        Assert.Equal(2, (await Store.Reader.ReadStreamAsync(Stream("occupe"), Ct)).Count);
    }

    [Fact]
    public async Task Chaque_evenement_a_sa_ligne_d_outbox_avec_l_enveloppe_complete()
    {
        var stored = await CommitOk(Event(Stream("a"), 1) with { BalanceAfter = 40m }, Event(Stream("b"), 1));

        var outbox = await Store.ReadOutboxAsync([.. stored.Select(e => e.EventId)]);

        Assert.Equal(2, outbox.Count);
        foreach (var message in outbox)
        {
            var expected = stored.Single(e => e.EventId == message.EventId);
            var envelope = message.Payload.Deserialize<EventEnvelopeV1>(EventJson.Options)!;
            Assert.Equal(OutboxMessages.Topic, message.Topic);
            Assert.Equal(_entity, message.PartitionKey);
            Assert.Equal(expected.GlobalSequence, envelope.GlobalSequence);
            Assert.Equal(expected.Hash, envelope.Hash);
            Assert.Equal(expected.StreamId, envelope.StreamId);
            Assert.Equal(expected.RecordedAt, envelope.RecordedAt);
            Assert.Equal(JsonCanonicalizer.Canonicalize(expected.Payload), JsonCanonicalizer.Canonicalize(envelope.Payload));
        }

        Assert.Equal("40.00", outbox.Single(m => m.EventId == stored[0].EventId).Payload.GetProperty("balanceAfter").GetString());
    }

    [Fact]
    public async Task Aucune_ecriture_dans_une_periode_scellee()
    {
        await CommitOk(Event(PeriodStream("2026-03"), 1, EventTypes.PeriodOpened), Event(PeriodStream("2026-03"), 2, EventTypes.PeriodSealed));

        var result = await Store.BeginUnitOfWork().CommitAsync(Event(Stream("a"), 1));

        AssertRejected(result, EventStoreErrors.PeriodSealedCode);
        Assert.Empty(await Store.Reader.ReadStreamAsync(Stream("a"), Ct));
    }

    [Fact]
    public async Task Le_sceau_ne_concerne_que_sa_periode_et_son_entite()
    {
        await CommitOk(Event(PeriodStream("2026-03"), 1, EventTypes.PeriodSealed));

        await CommitOk(Event(Stream("avril"), 1) with { Period = "2026-04", BusinessDate = new DateOnly(2026, 4, 1) });
        await CommitOk(Event(Stream("autre-entite"), 1) with { EntityId = _entity + "-B" });
    }

    [Fact]
    public async Task Un_evenement_place_apres_le_sceau_dans_le_meme_lot_est_refuse()
    {
        var result = await Store.BeginUnitOfWork().CommitAsync(
            Event(PeriodStream("2026-03"), 1, EventTypes.PeriodSealed),
            Event(Stream("a"), 1));

        AssertRejected(result, EventStoreErrors.PeriodSealedCode);
        Assert.Empty(await Store.Reader.ReadStreamAsync(PeriodStream("2026-03"), Ct));
    }

    public static TheoryData<string, DateOnly?, decimal?> EvenementsInvalides => new()
    {
        { "2026-03", null, 1.005m },
        { "2026-03", null, 1_000_000_000_000_000_000m },
        { "2026-3", null, null },
        { "2026-13", null, null },
        { "2026-03-01", null, null },
        { "2026-04", new DateOnly(2026, 3, 31), null },
    };

    [Theory]
    [MemberData(nameof(EvenementsInvalides))]
    public async Task Un_evenement_non_stockable_est_un_bogue_de_l_appelant(string period, DateOnly? businessDate, decimal? balanceAfter)
    {
        var invalid = Event(Stream("a"), 1) with { Period = period, BusinessDate = businessDate, BalanceAfter = balanceAfter };

        await Assert.ThrowsAsync<ArgumentException>(() => Store.BeginUnitOfWork().CommitAsync(invalid));
        Assert.Empty(await Store.Reader.ReadStreamAsync(Stream("a"), Ct));
    }

    protected EventToAppend Event(string streamId, long version, string eventType = "TestEvent") => new(
        Guid.CreateVersion7(),
        streamId,
        version,
        eventType,
        1,
        _entity,
        new DateOnly(2026, 3, 15),
        "2026-03",
        Json($$"""{"stream":"{{streamId}}","version":{{version}},"amount":"10.00"}"""),
        BalanceAfter: null,
        new EventMetadata(Guid.CreateVersion7(), null, "tests"));

    protected string Stream(string name) => $"test-{_entity}-{name}";

    private string PeriodStream(string period) => $"period-{_entity}-{period}";

    private async Task<IReadOnlyList<StoredEvent>> CommitOk(params EventToAppend[] events)
    {
        var result = await Store.BeginUnitOfWork().CommitAsync(events);
        Assert.True(result.IsSuccess, result.IsFailure ? $"{result.Error.Code} — {result.Error.Message}" : null);
        return result.Value;
    }

    private static void AssertRejected(Result<IReadOnlyList<StoredEvent>> result, string code)
    {
        Assert.True(result.IsFailure, "La validation aurait dû être rejetée.");
        Assert.Equal(code, result.Error.Code);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
