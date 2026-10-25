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
        Types().That().ResideInAssembly(CommandApplication, ProjectionsApplication, QueryApplication, PkiApplication)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(InfrastructureLayerNamespaces)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(CompositionNamespaces)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(InfrastructureNamespaces)
            .Check(Model);

    [Fact]
    public void Infrastructure_ne_depend_pas_de_la_composition() =>
        Types().That().ResideInAssembly(CommandInfrastructure, ProjectionsInfrastructure, QueryInfrastructure, PkiInfrastructure)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(CompositionNamespaces)
            .Check(Model);

    [Fact]
    public void La_composition_ne_touche_pas_directement_la_base_ni_Kafka_ni_AWS() =>
        Types().That().ResideInAssembly(CommandApi, ProjectionsWorker, QueryApi, PkiSentinelle)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(DataAccessNamespaces)
            .Check(Model);

    [Fact]
    public void La_composition_Lambda_ne_touche_AWS_que_par_l_infrastructure() =>
        Types().That().ResideInAssembly(PkiLambda)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(DataAccessNamespacesHorsTransportLambda)
            .Check(Model);

    [Fact]
    public void La_PKI_ne_depend_d_aucun_service_du_grand_livre() =>
        Types().That().ResideInNamespaceMatching(@"^Ledger\.Pki(\..*)?$")
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^Ledger\.(Command|Projections|Query|Authorizer|Contracts)(\..*)?$")
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
    public void Query_ne_depend_pas_du_service_de_projections() =>
        Types().That().ResideInNamespaceMatching(@"^Ledger\.Query(\..*)?$")
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^Ledger\.(Projections|Authorizer)(\..*)?$")
            .Check(Model);

    [Fact]
    public void Projections_et_Query_ne_dependent_pas_du_service_de_commandes() =>
        Types().That().ResideInNamespaceMatching(@"^Ledger\.(Projections|Query)(\..*)?$")
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(@"^Ledger\.Command(\..*)?$")
            .Check(Model);
}
