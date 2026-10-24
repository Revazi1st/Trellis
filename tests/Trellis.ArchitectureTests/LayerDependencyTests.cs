using NetArchTest.Rules;
using Shouldly;
using Xunit;

namespace Trellis.ArchitectureTests;

/// <summary>
/// Enforces the dependency flow: SharedKernel ← Domain ← Application ← { Infrastructure ∥ Persistence } ← Presentation,
/// and inside Presentation Api → Api.Common → Api.Contracts ← UI.Common (Contracts is the BCL-only leaf both halves share).
/// </summary>
public sealed class LayerDependencyTests
{
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
}
