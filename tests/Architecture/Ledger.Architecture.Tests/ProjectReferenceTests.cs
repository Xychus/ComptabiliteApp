using System.Xml.Linq;

namespace Ledger.Architecture.Tests;

/// <summary>
/// Vérifie les références déclarées dans les <c>.csproj</c>. Nécessaire en plus d'ArchUnitNET :
/// une <c>PackageReference</c> encore inutilisée n'apparaît pas dans les métadonnées de l'assembly,
/// mais elle ouvre la porte à une dépendance interdite (ADR 0002).
/// </summary>
public sealed class ProjectReferenceTests
{
    private static readonly string[] _infrastructurePackagePrefixes =
        ["Npgsql", "Confluent.Kafka", "AWSSDK.", "Amazon.", "Microsoft.AspNetCore.", "Microsoft.EntityFrameworkCore", "Dapper", "dbup", "Testcontainers"];

    public static TheoryData<string> DomainProjects => ProjectsMatching(".Domain");

    public static TheoryData<string> ApplicationProjects => ProjectsMatching(".Application");

    [Theory]
    [MemberData(nameof(DomainProjects))]
    public void Domain_ne_reference_aucun_paquet(string project) =>
        Assert.Empty(PackageReferences(project));

    [Theory]
    [MemberData(nameof(DomainProjects))]
    public void Domain_ne_reference_que_SharedKernel(string project) =>
        Assert.All(ProjectReferences(project), reference => Assert.Equal("Ledger.SharedKernel", reference));

    [Theory]
    [MemberData(nameof(ApplicationProjects))]
    public void Application_ne_reference_ni_infrastructure_ni_composition(string project) =>
        Assert.All(ProjectReferences(project), reference =>
            Assert.False(
                reference.EndsWith(".Infrastructure", StringComparison.Ordinal)
                    || reference.EndsWith(".Api", StringComparison.Ordinal)
                    || reference.EndsWith(".Worker", StringComparison.Ordinal),
                $"{project} référence {reference}"));

    [Theory]
    [MemberData(nameof(ApplicationProjects))]
    public void Application_ne_reference_aucun_paquet_d_infrastructure(string project) =>
        Assert.All(PackageReferences(project), package =>
            Assert.DoesNotContain(_infrastructurePackagePrefixes, prefix => package.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public void SharedKernel_ne_reference_aucun_projet()
    {
        var project = Assert.Single(ProjectsMatching(".SharedKernel"));
        Assert.Empty(ProjectReferences(project.Data));
    }

    [Fact]
    public void Les_projets_de_domaine_sont_bien_decouverts() =>
        Assert.Contains(ProjectsMatching(".Domain"), row => row.Data == "Ledger.Command.Domain");

    private static TheoryData<string> ProjectsMatching(string suffix)
    {
        var data = new TheoryData<string>();
        foreach (var file in AllProjectFiles().Keys.Where(name => name.EndsWith(suffix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            data.Add(file);
        }

        return data;
    }

    private static IEnumerable<string> PackageReferences(string project) =>
        Load(project).Descendants("PackageReference").Select(e => (string?)e.Attribute("Include") ?? string.Empty);

    private static IEnumerable<string> ProjectReferences(string project) =>
        Load(project).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(((string?)e.Attribute("Include") ?? string.Empty).Replace('\\', '/')));

    private static XDocument Load(string project) => XDocument.Load(AllProjectFiles()[project]);

    private static Dictionary<string, string> AllProjectFiles() =>
        Directory.EnumerateFiles(Path.Join(RepositoryRoot(), "src"), "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(path => Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Ledger.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Racine du dépôt introuvable (Ledger.slnx).");
    }
}
