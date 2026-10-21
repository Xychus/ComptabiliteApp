using Ledger.Contracts.Events.V1;
using Ledger.Projections.Application;
using Ledger.Projections.Application.Processing;
using Ledger.Projections.Application.Rebuild;
using Ledger.Projections.Application.Taxes;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Projections.Infrastructure.Tests.Contract;

/// <summary>
/// Une implémentation des ports des projections, vue par la suite de contrat : journal (source de
/// vérité), worker complet (session, magasins, rejeu) et lecture canonique des tables d'une entité.
/// </summary>
public abstract class ProjectionBackend : IAsyncDisposable
{
    public const string Topic = "ledger.events";

    private ServiceProvider? _services;
    private long _nextOffset;

    /// <summary>Groupe de consommateurs propre à chaque test : les checkpoints ne se mélangent pas.</summary>
    public string ConsumerGroup { get; } = "contrat-" + Guid.CreateVersion7().ToString("N");

    /// <summary>Entités propres à chaque test (la base PostgreSQL est partagée par la collection).</summary>
    public string Entity { get; } = "ACME-" + Guid.CreateVersion7().ToString("N");

    public string OtherEntity { get; } = "AUTRE-" + Guid.CreateVersion7().ToString("N");

    /// <summary>Comptes fiscaux de <see cref="Entity"/>.</summary>
    public TaxAccountMap Taxes => new(
    [
        new TaxAccount(Entity, "2310", TaxKind.GstCollected),
        new TaxAccount(Entity, "2320", TaxKind.QstCollected),
        new TaxAccount(Entity, "1350", TaxKind.GstPaid),
        new TaxAccount(Entity, "1360", TaxKind.QstPaid),
    ]);

    public IServiceProvider Services => _services ??= Build();

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Valide l'événement dans le journal ; renvoie l'enveloppe stockée (séquence du serveur).</summary>
    public abstract Task<EventEnvelopeV1> RecordAsync(EventEnvelopeV1 envelope);

    /// <summary>Contenu canonique des tables d'une projection, restreint à une entité.</summary>
    public abstract Task<string> DumpAsync(string projection, string entityId);

    /// <summary>Position (séquence) d'un jeu de tables : projection en service, ou slot de reconstruction.</summary>
    public abstract Task<long?> PositionAsync(string slot);

    /// <summary>Débits et crédits cumulés d'un compte, ou rien.</summary>
    public abstract Task<(decimal Debit, decimal Credit)?> BalanceAsync(string entityId, string accountId);

    public async Task<IReadOnlyList<EventEnvelopeV1>> RecordAllAsync(IEnumerable<EventEnvelopeV1> events)
    {
        var stored = new List<EventEnvelopeV1>();
        foreach (var envelope in events)
        {
            stored.Add(await RecordAsync(envelope));
        }

        return stored;
    }

    /// <summary>Valide puis livre chaque événement, dans l'ordre.</summary>
    public async Task<IReadOnlyList<EventEnvelopeV1>> DeliverAsync(params EventEnvelopeV1[] events)
    {
        var stored = await RecordAllAsync(events);
        await RedeliverAsync([.. stored]);
        return stored;
    }

    /// <summary>Livre des événements déjà validés, chacun à un nouvel offset de la partition 0.</summary>
    public async Task RedeliverAsync(params EventEnvelopeV1[] events)
    {
        var pump = Services.GetRequiredService<ProjectionPump>();
        foreach (var envelope in events)
        {
            await pump.ProcessAsync(new SourceMessage(envelope, new SourcePosition(ConsumerGroup, Topic, 0, _nextOffset++)), Ct);
        }
    }

    public Task<RebuildReport> RebuildAsync(string projection) =>
        Services.GetRequiredService<ProjectionRebuilder>().RebuildAsync(projection, Ct);

    public async ValueTask DisposeAsync()
    {
        if (_services is not null)
        {
            await _services.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    protected abstract void AddStore(IServiceCollection services);

    private ServiceProvider Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLedgerProjections(Taxes);
        AddStore(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}
