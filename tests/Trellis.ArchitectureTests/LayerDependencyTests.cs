using System.Reflection;
using System.Text.RegularExpressions;
using NetArchTest.Rules;
using Shouldly;
using Xunit;

namespace Trellis.ArchitectureTests;

/// <summary>
/// Enforces the dependency flow: SharedKernel ← Domain ← Application ← { Infrastructure ∥ Persistence } ← Presentation,
/// and inside Presentation Api → Api.Common → Api.Contracts ← UI.Common (Contracts is the BCL-only leaf both halves share).
/// ServiceDefaults, the hosts' shared plumbing, depends on no layer at all; and no project under <c>src/</c>
/// escapes these rules by never being loaded.
/// </summary>
public sealed partial class LayerDependencyTests
{
    /// <summary>
    /// The projects under <c>src/</c> that need no anchor in <see cref="Architecture"/>, keyed by project name,
    /// each with its reason. An entry the solution no longer lists, or that has since been anchored, fails
    /// the check, so the list stays true.
    /// </summary>
    private static readonly Dictionary<string, string> UnanchoredProjectAllowlist = new(StringComparer.Ordinal)
    {
#if (IncludeUI)
        ["Trellis.UI.Web"] = "a host, booted by WebHostTests",
#endif
    };

    [Fact]
    public void Every_project_under_src_is_anchored_in_ArchitectureRules()
    {
        // A project no rule here loads is a layer no rule guards: it may reference anything, and nothing
        // fails. Each project under src/ in the solution is anchored by an Assembly property on Architecture
        // and given its rule in this class, or allowlisted with the reason it needs none.
        List<(string Location, string Name)> projects = [.. RepositorySource.Root.EnumerateFiles("*.slnx")
            .SelectMany(static solution => ProjectsUnderSrc(solution.Name, File.ReadAllText(solution.FullName)))];
        HashSet<string> anchored = AnchoredAssemblyNames();

        projects.ShouldNotBeEmpty("no <Project Path=\"src/…\"> found in the solution at the repository root; has its shape changed?");
        UnanchoredProjectAllowlist.Keys
            .Where(name => anchored.Contains(name) || !projects.Exists(project => string.Equals(project.Name, name, StringComparison.Ordinal)))
            .Select(static name => $"{name}: on the allowlist, but the solution no longer lists it or Architecture now anchors it; remove the entry")
            .ShouldBeEmpty();
        UnanchoredProjects(projects, anchored).ShouldBeEmpty(
            "anchor the project with an Assembly property on Architecture (ArchitectureRules.cs) and give it its rule in LayerDependencyTests, "
            + "or allowlist it in UnanchoredProjectAllowlist with the reason it needs none");
    }

    /// <summary>
    /// Positive control for the anchor check, which expects zero hits: a planted solution naming a project
    /// under <c>src/</c> that nothing anchors is flagged at its line, inside a conditional block as outside
    /// one; an anchored project, an allowlisted one and a project outside <c>src/</c> are not.
    /// </summary>
    [Theory]
    [InlineData("<Project Path=\"src/Core/Trellis.Billing/Trellis.Billing.csproj\" />", "Trellis.Billing")]
    [InlineData("<Project Path=\"src/Core/Trellis.Domain/Trellis.Domain.csproj\" />", null)]
    [InlineData("<Project Path=\"src/Shared/Trellis.ServiceDefaults/Trellis.ServiceDefaults.csproj\" />", null)]
    [InlineData("<Project Path=\"tests/Trellis.Billing.Tests/Trellis.Billing.Tests.csproj\" />", null)]
#if (IncludeUI)
    [InlineData("<Project Path=\"src/Presentation/UI/Trellis.UI.Web/Trellis.UI.Web.csproj\" />", null)]
#endif
    public void The_anchor_check_flags_a_project_under_src_that_no_rule_loads(string projectLine, string? unanchored)
    {
        // The directive pair is assembled so this file carries no template token of its own.
        string planted = string.Join('\n',
            "<Solution>",
            "  <Folder Name=\"/src/\">",
            "    <!--#" + "if (IncludeUI) -->",
            "    " + projectLine,
            "    <!--#" + "endif -->",
            "  </Folder>",
            "</Solution>");

        List<string> violations = UnanchoredProjects(ProjectsUnderSrc("Planted.slnx", planted), AnchoredAssemblyNames());

        if (unanchored is null)
        {
            violations.ShouldBeEmpty();
        }
        else
        {
            violations.ShouldHaveSingleItem().ShouldStartWith($"Planted.slnx:4: {unanchored} is not anchored");
        }
    }

    [Fact]
    public void ServiceDefaults_depends_on_no_solution_layer()
        => Types.InAssembly(Architecture.ServiceDefaultsAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                Architecture.SharedKernel,
                Architecture.Domain,
                Architecture.Application,
                Architecture.Infrastructure,
                Architecture.Persistence,
                Architecture.Presentation,
                Architecture.Ui)
            .GetResult()
            .ShouldBeCompliant();

    [Fact]
    public void SharedKernel_depends_on_no_other_solution_layer()
        => Types.InAssembly(Architecture.SharedKernelAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                Architecture.Domain,
                Architecture.Application,
                Architecture.Infrastructure,
                Architecture.Persistence,
                Architecture.Presentation)
            .GetResult()
            .ShouldBeCompliant();

    [Fact]
    public void Domain_depends_only_on_SharedKernel()
        => DomainRule(Architecture.DomainAssembly, Architecture.Domain).ShouldBeCompliant();

    [Fact]
    public void The_domain_rule_refuses_what_the_SharedKernel_brings_along()
    {
        // The positive control: the same rule over the planted shapes, the test assembly's namespace standing in
        // for the Domain's own.
        Type[] refused = [.. (DomainRule(typeof(PlantedViolations).Assembly, typeof(PlantedViolations).Namespace!).FailingTypes ?? [])
            .Select(static type => type.ReflectionType)
            .OfType<Type>()];

        refused.ShouldContain(typeof(PlantedViolations.ValidatorInTheDomain));
        refused.ShouldContain(typeof(PlantedViolations.LoggerInTheDomain));
        refused.ShouldNotContain(typeof(PlantedViolations.Command), "a shape on the BCL and the SharedKernel alone passes");
    }

    [Fact]
    public void Application_does_not_depend_on_outer_layers_or_infrastructure_concerns()
        => Types.InAssembly(Architecture.ApplicationAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                Architecture.Infrastructure,
                Architecture.Persistence,
                Architecture.Presentation,
                Architecture.EntityFramework,
                Architecture.AspNetCore)
            .GetResult()
            .ShouldBeCompliant();

    [Fact]
    public void Infrastructure_does_not_depend_on_Persistence()
        => Types.InAssembly(Architecture.InfrastructureAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(Architecture.Persistence)
            .GetResult()
            .ShouldBeCompliant();

    [Fact]
    public void Persistence_does_not_depend_on_Infrastructure()
        => Types.InAssembly(Architecture.PersistenceAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(Architecture.Infrastructure)
            .GetResult()
            .ShouldBeCompliant();

    [Theory]
    [MemberData(nameof(InnerLayers))]
    public void Inner_layers_do_not_depend_on_presentation(string layer)
        => Types.InAssembly(AssemblyFor(layer))
            .ShouldNot()
            .HaveDependencyOnAny(Architecture.Presentation, Architecture.Ui)
            .GetResult()
            .ShouldBeCompliant();

#if (IncludeUI)
    [Fact]
    public void Ui_talks_to_the_api_only_through_contracts_never_inner_layers()
        => Types.InAssembly(Architecture.UiCommonAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                Architecture.SharedKernel,
                Architecture.Domain,
                Architecture.Application,
                Architecture.Infrastructure,
                Architecture.Persistence,
                Architecture.Presentation + ".Common",   // Api.Common is host plumbing, not a UI dependency
                Architecture.EntityFramework)
            .GetResult()
            .ShouldBeCompliant();
#endif

    [Fact]
    public void Api_contracts_are_a_pure_dependency_free_surface()
        => Types.InAssembly(Architecture.ApiContractsAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                Architecture.SharedKernel,
                Architecture.Domain,
                Architecture.Application,
                Architecture.Infrastructure,
                Architecture.Persistence,
                Architecture.Presentation + ".Common",   // Api.Common references Contracts, never the reverse (ADR-0024)
                Architecture.Ui,
                Architecture.EntityFramework,
                Architecture.AspNetCore)
            .GetResult()
            .ShouldBeCompliant();

    /// <summary>
    /// The Domain's rule as an allow-list: a type may depend on the BCL (<c>System.*</c>), the SharedKernel and
    /// its own assembly's namespace, nothing else. Not a list of what is banned: the SharedKernel carries
    /// FluentValidation and the Microsoft.Extensions DI and logging abstractions, so the Domain compiles against
    /// all three, and the deny-list this replaced (the outer layers, EF Core, ASP.NET Core) let a Domain type
    /// derive from <c>AbstractValidator</c> or take an <c>ILogger</c> unnoticed.
    /// </summary>
    private static NetArchTest.Rules.TestResult DomainRule(System.Reflection.Assembly assembly, string ownNamespace)
        => Types.InAssembly(assembly)
            .ShouldNot()
            .HaveDependencyOtherThan("System", Architecture.SharedKernel, ownNamespace)
            .GetResult();

    public static TheoryData<string> InnerLayers() =>
        [Architecture.SharedKernel, Architecture.Domain, Architecture.Application, Architecture.Infrastructure, Architecture.Persistence];

    private static System.Reflection.Assembly AssemblyFor(string layer) => layer switch
    {
        Architecture.SharedKernel => Architecture.SharedKernelAssembly,
        Architecture.Domain => Architecture.DomainAssembly,
        Architecture.Application => Architecture.ApplicationAssembly,
        Architecture.Infrastructure => Architecture.InfrastructureAssembly,
        Architecture.Persistence => Architecture.PersistenceAssembly,
        _ => throw new ArgumentOutOfRangeException(nameof(layer), layer, "Unknown layer."),
    };

    /// <summary>The names of the assemblies <see cref="Architecture"/>'s <see cref="Assembly"/> properties anchor, read by reflection.</summary>
    private static HashSet<string> AnchoredAssemblyNames()
        => new(typeof(Architecture).GetProperties(BindingFlags.Public | BindingFlags.Static)
                .Where(static property => property.PropertyType == typeof(Assembly))
                .Select(static property => ((Assembly)property.GetValue(null)!).GetName().Name!),
            StringComparer.Ordinal);

    /// <summary>
    /// Each <c>&lt;Project Path="src/…/X.csproj"&gt;</c> of a solution file as its <c>file:line</c> and its
    /// project name; the template's conditional lines are dropped, the projects between them kept.
    /// </summary>
    private static IEnumerable<(string Location, string Name)> ProjectsUnderSrc(string fileName, string solution)
        => solution.ReplaceLineEndings("\n").Split('\n')
            .Select(static (line, index) => (Line: line, Number: index + 1))
            .Where(static line => !TemplateDirective().IsMatch(line.Line))
            .Select(static line => (line.Number, Match: ProjectUnderSrc().Match(line.Line)))
            .Where(static line => line.Match.Success)
            .Select(line => ($"{fileName}:{line.Number}", Path.GetFileNameWithoutExtension(line.Match.Groups["path"].Value.Replace('\\', '/'))));

    private static List<string> UnanchoredProjects(IEnumerable<(string Location, string Name)> projects, HashSet<string> anchored)
        => [.. projects
            .Where(project => !anchored.Contains(project.Name) && !UnanchoredProjectAllowlist.ContainsKey(project.Name))
            .Select(static project => $"{project.Location}: {project.Name} is not anchored in ArchitectureRules.cs")];

    /// <summary>A template conditional line of a solution file (the opening or closing comment).</summary>
    [GeneratedRegex(@"<!--#(?:if|endif)\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TemplateDirective();

    [GeneratedRegex(@"<Project\s+Path\s*=\s*""(?<path>src[/\\][^""]+\.csproj)""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ProjectUnderSrc();
}
