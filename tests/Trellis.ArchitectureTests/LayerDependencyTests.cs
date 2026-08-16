using NetArchTest.Rules;
using Xunit;

namespace Trellis.ArchitectureTests;

/// <summary>Enforces the dependency flow: SharedKernel ← Domain ← Application ← { Infrastructure ∥ Persistence } ← Presentation.</summary>
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
        => Types.InAssembly(Architecture.DomainAssembly)
            .ShouldNot()
            .HaveDependencyOnAny(
                Architecture.Application,
                Architecture.Infrastructure,
                Architecture.Persistence,
                Architecture.Presentation,
                Architecture.EntityFramework,
                Architecture.AspNetCore)
            .GetResult()
            .ShouldBeCompliant();

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
                Architecture.EntityFramework,
                Architecture.AspNetCore)
            .GetResult()
            .ShouldBeCompliant();

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
