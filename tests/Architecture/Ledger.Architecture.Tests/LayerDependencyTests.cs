using ArchUnitNET.xUnitV3;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using static Ledger.Architecture.Tests.LedgerArchitecture;

namespace Ledger.Architecture.Tests;

/// <summary>
/// Règles de dépendances entre types (ADR 0002). Elles détectent l'usage effectif d'un type interdit ;
/// les références de projet et de paquet sont vérifiées par <see cref="ProjectReferenceTests"/>.
/// </summary>
public sealed class LayerDependencyTests
{
    [Fact]
    public void SharedKernel_ne_depend_d_aucun_autre_projet_Ledger() =>
        Types().That().ResideInAssembly(SharedKernelAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^Ledger\.(?!SharedKernel(\.|$)).*$")
            .Check(Model);

    [Fact]
    public void Domain_ne_depend_que_de_SharedKernel() =>
        Types().That().ResideInAssembly(CommandDomain)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^Ledger\.(?!SharedKernel(\.|$)|Command\.Domain(\.|$)).*$")
            .Check(Model);

    [Fact]
    public void Domain_ne_depend_d_aucune_technologie_d_infrastructure() =>
        Types().That().ResideInAssembly(CommandDomain)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(InfrastructureNamespaces)
            .Check(Model);

    [Fact]
    public void Application_ne_depend_ni_de_l_infrastructure_ni_de_la_composition() =>
        Types().That().ResideInAssembly(CommandApplication, ProjectionsApplication)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(InfrastructureLayerNamespaces)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(CompositionNamespaces)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(InfrastructureNamespaces)
            .Check(Model);

    [Fact]
    public void Infrastructure_ne_depend_pas_de_la_composition() =>
        Types().That().ResideInAssembly(CommandInfrastructure, ProjectionsInfrastructure)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(CompositionNamespaces)
            .Check(Model);

    [Fact]
    public void La_composition_ne_touche_pas_directement_la_base_ni_Kafka_ni_AWS() =>
        Types().That().ResideInAssembly(CommandApi, ProjectionsWorker, QueryApi)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(DataAccessNamespaces)
            .Check(Model);

    [Fact]
    public void Contracts_ne_depend_d_aucune_couche_de_service() =>
        Types().That().ResideInAssembly(ContractsAssembly)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(DomainLayerNamespaces)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(ApplicationLayerNamespaces)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(InfrastructureLayerNamespaces)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(CompositionNamespaces)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(InfrastructureNamespaces)
            .Check(Model);

    [Fact]
    public void Command_ne_depend_d_aucun_autre_service() =>
        Types().That().ResideInNamespaceMatching(@"^Ledger\.Command(\..*)?$")
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^Ledger\.(Projections|Query|Authorizer)(\..*)?$")
            .Check(Model);

    [Fact]
    public void Projections_et_Query_ne_dependent_pas_du_service_de_commandes() =>
        Types().That().ResideInNamespaceMatching(@"^Ledger\.(Projections|Query)(\..*)?$")
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^Ledger\.Command(\..*)?$")
            .Check(Model);
}
