using System.Reflection;
using ArchUnitNET.Loader;

namespace Ledger.Architecture.Tests;

/// <summary>Assemblies de production chargées une seule fois pour toutes les règles.</summary>
internal static class LedgerArchitecture
{
    public static readonly Assembly SharedKernelAssembly = typeof(Ledger.SharedKernel.AssemblyMarker).Assembly;
    public static readonly Assembly ContractsAssembly = typeof(Ledger.Contracts.AssemblyMarker).Assembly;
    public static readonly Assembly CommandDomain = typeof(Ledger.Command.Domain.AssemblyMarker).Assembly;
    public static readonly Assembly CommandApplication = typeof(Ledger.Command.Application.AssemblyMarker).Assembly;
    public static readonly Assembly CommandInfrastructure = typeof(Ledger.Command.Infrastructure.AssemblyMarker).Assembly;
    public static readonly Assembly CommandApi = typeof(Ledger.Command.Api.AssemblyMarker).Assembly;
    public static readonly Assembly ProjectionsApplication = typeof(Ledger.Projections.Application.AssemblyMarker).Assembly;
    public static readonly Assembly ProjectionsInfrastructure = typeof(Ledger.Projections.Infrastructure.AssemblyMarker).Assembly;
    public static readonly Assembly ProjectionsWorker = typeof(Ledger.Projections.Worker.AssemblyMarker).Assembly;
    public static readonly Assembly QueryApi = typeof(Ledger.Query.Api.AssemblyMarker).Assembly;
    public static readonly Assembly Authorizer = typeof(Ledger.Authorizer.AssemblyMarker).Assembly;
    public static readonly Assembly Migrations = typeof(Ledger.Migrations.AssemblyMarker).Assembly;
    public static readonly Assembly Integrity = typeof(Ledger.Integrity.AssemblyMarker).Assembly;

    public static readonly ArchUnitNET.Domain.Architecture Model = new ArchLoader()
        .LoadAssemblies(
            SharedKernelAssembly,
            ContractsAssembly,
            CommandDomain,
            CommandApplication,
            CommandInfrastructure,
            CommandApi,
            ProjectionsApplication,
            ProjectionsInfrastructure,
            ProjectionsWorker,
            QueryApi,
            Authorizer,
            Migrations,
            Integrity)
        .Build();

    /// <summary>Espaces de noms des technologies d'infrastructure, interdits hors de <c>*.Infrastructure</c> et de la composition.</summary>
    public const string InfrastructureNamespaces =
        @"^(Npgsql|Confluent\.Kafka|Amazon|Microsoft\.AspNetCore|Microsoft\.EntityFrameworkCore|Dapper|DbUp|Testcontainers)(\..*)?$";

    /// <summary>Accès aux données et aux services externes : réservés à <c>*.Infrastructure</c>, même pour la composition.</summary>
    public const string DataAccessNamespaces = @"^(Npgsql|Confluent\.Kafka|Amazon|Microsoft\.EntityFrameworkCore|Dapper|DbUp)(\..*)?$";

    /// <summary>Couches de composition et de transport : rien ne doit en dépendre.</summary>
    public const string CompositionNamespaces = @"^Ledger\.[A-Za-z]+\.(Api|Worker)(\..*)?$";

    public const string InfrastructureLayerNamespaces = @"^Ledger\.[A-Za-z]+\.Infrastructure(\..*)?$";

    public const string ApplicationLayerNamespaces = @"^Ledger\.[A-Za-z]+\.Application(\..*)?$";

    public const string DomainLayerNamespaces = @"^Ledger\.[A-Za-z]+\.Domain(\..*)?$";
}
