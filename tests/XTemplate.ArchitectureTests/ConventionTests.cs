using NetArchTest.Rules;
using XTemplate.SharedKernel.Messaging;
using Xunit;

namespace XTemplate.ArchitectureTests;

/// <summary>Naming and sealing conventions that keep the feature-slice pattern uniform for humans and agents.</summary>
public sealed class ConventionTests
{
    [Fact]
    public void Command_and_query_handlers_are_sealed()
        => Types.InAssembly(Architecture.ApplicationAssembly)
            .That()
            .ImplementInterface(typeof(ICommandHandler<>))
            .Or().ImplementInterface(typeof(ICommandHandler<,>))
            .Or().ImplementInterface(typeof(IQueryHandler<,>))
            .Should()
            .BeSealed()
            .GetResult()
            .ShouldBeCompliant();

    [Fact]
    public void Command_and_query_handlers_are_not_public()
        => Types.InAssembly(Architecture.ApplicationAssembly)
            .That()
            .ImplementInterface(typeof(ICommandHandler<>))
            .Or().ImplementInterface(typeof(ICommandHandler<,>))
            .Or().ImplementInterface(typeof(IQueryHandler<,>))
            .Should()
            .NotBePublic()
            .GetResult()
            .ShouldBeCompliant();
}
