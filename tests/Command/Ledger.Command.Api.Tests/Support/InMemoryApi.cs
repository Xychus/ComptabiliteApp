using Ledger.Command.Infrastructure.InMemory;
using Microsoft.Extensions.DependencyInjection;

namespace Ledger.Command.Api.Tests.Support;

/// <summary>Application partagée par les tests sans Docker : event store en mémoire.</summary>
public sealed class InMemoryApi : IAsyncDisposable
{
    public LedgerApiFactory Factory { get; } = new();

    public InMemoryEventStore Store => Factory.Services.GetRequiredService<InMemoryEventStore>();

    public LedgerClient NewClient(string? token = null) => new(Factory.CreateAuthenticatedClient(token));

    public ValueTask DisposeAsync() => Factory.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class InMemoryApiGroup : ICollectionFixture<InMemoryApi>
{
    public const string Name = "api-en-memoire";
}
