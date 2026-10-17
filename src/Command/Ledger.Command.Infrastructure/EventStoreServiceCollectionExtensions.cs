using Ledger.Command.Application.EventStore;
using Ledger.Command.Infrastructure.InMemory;
using Ledger.Command.Infrastructure.Postgres;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ledger.Command.Infrastructure;

/// <summary>
/// Enregistrement des implémentations de l'event store. L'unité de travail est par portée
/// (une requête = une commande) ; les lecteurs sont partagés.
/// </summary>
public static class EventStoreServiceCollectionExtensions
{
    /// <summary>
    /// Event store PostgreSQL. La chaîne de connexion désigne le rôle <c>ledger_app</c>
    /// (<c>SSL Mode=VerifyFull</c> en AWS) ; elle vient de Secrets Manager, jamais du code, et
    /// n'est lue qu'à la première résolution de la source de données.
    /// </summary>
    public static IServiceCollection AddPostgresEventStore(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        services.AddSingleton(sp => NpgsqlDataSource.Create(connectionString(sp)));
        services.AddSingleton<NpgsqlEventStore>();
        services.AddSingleton<IEventReader>(sp => sp.GetRequiredService<NpgsqlEventStore>());
        services.AddSingleton<IPeriodEventReader>(sp => sp.GetRequiredService<NpgsqlEventStore>());
        services.AddScoped<NpgsqlUnitOfWork>();
        services.AddScoped<IEventAppender>(sp => sp.GetRequiredService<NpgsqlUnitOfWork>());
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<NpgsqlUnitOfWork>());
        services.AddSingleton<PostgresHealthCheck>();
        return services;
    }

    /// <summary>Event store en mémoire, perdu à l'arrêt : développement et tests uniquement.</summary>
    public static IServiceCollection AddInMemoryEventStore(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryEventStore>();
        services.AddSingleton<IEventReader>(sp => sp.GetRequiredService<InMemoryEventStore>());
        services.AddSingleton<IPeriodEventReader>(sp => sp.GetRequiredService<InMemoryEventStore>());
        services.AddScoped(sp => sp.GetRequiredService<InMemoryEventStore>().BeginUnitOfWork());
        services.AddScoped<IEventAppender>(sp => sp.GetRequiredService<InMemoryUnitOfWork>());
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<InMemoryUnitOfWork>());
        return services;
    }
}
