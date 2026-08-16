using System.Reflection;
using NetArchTest.Rules;
using Shouldly;
using XTemplate.Api.Common.Endpoints;
using XTemplate.SharedKernel.Domain;
using XTemplate.SharedKernel.Messaging;
using XTemplate.SharedKernel.Results;
using Xunit;

namespace XTemplate.ArchitectureTests;

/// <summary>
/// The conventions the layer rules state as mandatory, enforced instead of asserted in prose:
/// uniform slices are what keep the template navigable for humans and agents alike.
/// </summary>
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

    [Fact]
    public void Endpoints_are_internal_sealed_one_class_per_use_case()
        => Types.InAssembly(Architecture.ApiAssembly)
            .That()
            .ImplementInterface(typeof(IEndpoint))
            .Should()
            .BeSealed()
            .And().NotBePublic()
            .GetResult()
            .ShouldBeCompliant();

    [Fact]
    public void Value_objects_are_sealed_records_with_a_Create_factory_returning_Result()
    {
        List<Type> valueObjects = [.. Architecture.DomainAssembly.GetTypes()
            .Where(static t => t is { IsClass: true, IsAbstract: false, IsNested: false }
                               && IsRecord(t)
                               && !typeof(IDomainEvent).IsAssignableFrom(t))];

        valueObjects.ShouldNotBeEmpty();   // Money and EmailAddress anchor the convention

        foreach (Type valueObject in valueObjects)
        {
            valueObject.IsSealed.ShouldBeTrue($"{valueObject.Name} must be a sealed record (.claude/rules/domain.md)");

            MethodInfo? create = valueObject.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(static m => m.Name == "Create");
            create.ShouldNotBeNull($"{valueObject.Name} needs a static Create(...) factory");
            create.ReturnType.IsGenericType.ShouldBeTrue($"{valueObject.Name}.Create must return Result<T>");
            create.ReturnType.GetGenericTypeDefinition().ShouldBe(typeof(Result<>),
                $"{valueObject.Name}.Create must return Result<T>, not throw");
        }
    }

    [Fact]
    public void Domain_events_are_sealed_records()
    {
        List<Type> events = [.. Architecture.DomainAssembly.GetTypes()
            .Where(static t => t is { IsClass: true, IsAbstract: false } && typeof(IDomainEvent).IsAssignableFrom(t))];

        events.ShouldNotBeEmpty();
        events.ShouldAllBe(static e => e.IsSealed, "domain events are sealed records");
        events.ShouldAllBe(e => IsRecord(e), "domain events are records (value equality is what handlers and tests rely on)");
    }

    [Fact]
    public void Aggregates_and_entities_have_no_public_constructors_and_no_public_setters()
    {
        // Interceptor-stamped bookkeeping is the sanctioned exception: those setters exist for the
        // AuditingInterceptor/SoftDeleteInterceptor, never for callers.
        HashSet<string> interceptorProperties = [.. typeof(IAuditable).GetProperties()
            .Concat(typeof(ISoftDeletable).GetProperties())
            .Select(static p => p.Name)];

        List<Type> entities = [.. Architecture.DomainAssembly.GetTypes()
            .Where(static t => t is { IsClass: true, IsAbstract: false } && DerivesFromGeneric(t, typeof(Entity<>)))];

        entities.ShouldNotBeEmpty();

        foreach (Type entity in entities)
        {
            entity.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .ShouldBeEmpty($"{entity.Name} must be created through its static Create factory, not a public constructor");

            IEnumerable<PropertyInfo> publicSetters = entity
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.SetMethod is { IsPublic: true } && !interceptorProperties.Contains(p.Name));

            publicSetters.ShouldBeEmpty(
                $"{entity.Name} exposes public setters — mutate through intention-revealing methods instead");
        }
    }

    private static bool IsRecord(Type type)
        => type.GetProperty("EqualityContract", BindingFlags.NonPublic | BindingFlags.Instance) is not null;

    private static bool DerivesFromGeneric(Type type, Type openGeneric)
    {
        for (Type? current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == openGeneric)
            {
                return true;
            }
        }

        return false;
    }
}
