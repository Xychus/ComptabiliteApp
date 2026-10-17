using Ledger.Command.Application.Events;
using Ledger.Command.Application.EventStore;
using Ledger.Command.Application.Periods;
using Ledger.Command.Application.Tests.Support;
using Ledger.Command.Domain.Periods;
using Ledger.Contracts.Events.V1;
using Ledger.Contracts.Integrity;
using static Ledger.Command.Application.Tests.Support.InMemoryLedger;
using InMemoryLedger = Ledger.Command.Application.Tests.Support.InMemoryLedger;

namespace Ledger.Command.Application.Tests.Periods;

/// <summary>Ouverture et scellement, avec version attendue et empreinte des événements (ADR 0013).</summary>
public sealed class PeriodHandlerTests
{
    private readonly InMemoryLedger _ledger = new();

    [Fact]
    public async Task Ouvrir_une_periode_ecrit_PeriodOpened_sans_date_d_affaires()
    {
        var result = await _ledger.OpenAsync(March, expectedVersion: 0);

        var stored = Assert.Single(await _ledger.ReadAsync(StreamIds.Period(Acme, March)));
        Assert.Equal(EventTypes.PeriodOpened, stored.EventType);
        Assert.Null(stored.BusinessDate);
        Assert.Equal("2026-03", stored.Period);
        Assert.Equal(new PeriodReceipt(Acme, March, PeriodStatus.Open, 1, stored.GlobalSequence, stored.RecordedAt, stored.Hash, Digest: null), result.Value);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public async Task Une_version_attendue_differente_est_rejetee_sans_rien_ecrire(long expectedVersion)
    {
        var result = await _ledger.OpenAsync(March, expectedVersion);

        Assert.Equal(EventStoreErrors.WrongExpectedVersionCode, result.Error.Code);
        Assert.Equal("0", result.Error.Details!["actualVersion"]);
        Assert.Empty(await _ledger.ReadAsync(StreamIds.Period(Acme, March)));
    }

    [Fact]
    public async Task Le_decideur_rejette_une_periode_deja_ouverte()
    {
        await _ledger.OpenAsync(March, 0);

        Assert.Equal(PeriodErrors.AlreadyOpenCode, (await _ledger.OpenAsync(March, 1)).Error.Code);
    }

    [Fact]
    public async Task Sceller_publie_le_nombre_et_la_racine_de_Merkle_des_evenements_de_la_periode()
    {
        await _ledger.OpenAsync(March, 0);
        await _ledger.PostAsync(Entry(Debit(Cash, 100m), Credit(Sales, 100m)));
        var hashes = await _ledger.Store.ReadPeriodHashesAsync(Acme.Value, "2026-03", Ct);

        var result = await _ledger.SealAsync(March, expectedVersion: 1);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        var digest = new PeriodDigest(3, MerkleTree.Root(hashes));
        Assert.Equal(digest, result.Value.Digest);
        Assert.Equal((PeriodStatus.Sealed, 2L), (result.Value.Status, result.Value.Version));
        var sealedEvent = (await _ledger.ReadAsync(StreamIds.Period(Acme, March)))[1];
        Assert.Equal(new PeriodSealed(Acme, March, digest), EventCatalog.Default.Deserialize(sealedEvent.EventType, sealedEvent.SchemaVersion, sealedEvent.Payload));
    }

    [Fact]
    public async Task Une_ecriture_validee_pendant_le_calcul_du_sceau_relance_le_calcul()
    {
        await _ledger.OpenAsync(March, 0);
        var racingReader = new RacingPeriodReader(_ledger.Store, async () => Assert.True((await _ledger.PostAsync(Entry(Debit(Receivables, 1m), Credit(Sales, 1m)))).IsSuccess));
        var unitOfWork = new CountingUnitOfWork(_ledger.Store.BeginUnitOfWork());

        var result = await _ledger.SealHandler(unitOfWork, racingReader).HandleAsync(new PeriodCommand(Acme, March, 1), Metadata, Ct);

        Assert.True(result.IsSuccess, result.IsFailure ? result.Error.Message : null);
        Assert.Equal(2, unitOfWork.Commits);
        Assert.Equal(2, result.Value.Digest!.EventCount);
    }

    [Fact]
    public async Task Le_sceau_n_est_pas_retente_au_dela_de_la_limite()
    {
        await _ledger.OpenAsync(March, 0);
        var conflicting = new AlwaysConflictingUnitOfWork();

        var result = await _ledger.SealHandler(conflicting, maxAttempts: 2).HandleAsync(new PeriodCommand(Acme, March, 1), Metadata, Ct);

        Assert.Equal(EventStoreErrors.ConcurrencyConflictCode, result.Error.Code);
        Assert.Equal(2, conflicting.Commits);
    }

    [Fact]
    public async Task Sceller_avec_une_version_perimee_est_rejete()
    {
        await _ledger.OpenAsync(March, 0);

        Assert.Equal(EventStoreErrors.WrongExpectedVersionCode, (await _ledger.SealAsync(March, 0)).Error.Code);
    }

    [Fact]
    public async Task Sceller_une_periode_jamais_ouverte_est_rejete() =>
        Assert.Equal(PeriodErrors.NotOpenCode, (await _ledger.SealAsync(March, 0)).Error.Code);

    /// <summary>Fait valider une écriture concurrente juste après la première lecture des hash.</summary>
    private sealed class RacingPeriodReader(IPeriodEventReader inner, Func<Task> competitor) : IPeriodEventReader
    {
        private bool _raced;

        public async Task<IReadOnlyList<string>> ReadPeriodHashesAsync(string entityId, string period, CancellationToken cancellationToken)
        {
            var hashes = await inner.ReadPeriodHashesAsync(entityId, period, cancellationToken);
            if (!_raced)
            {
                _raced = true;
                await competitor();
            }

            return hashes;
        }
    }
}
