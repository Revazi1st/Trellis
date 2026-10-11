using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Trellis.Application.Features.Auth.Login;
using Trellis.SharedKernel.Messaging;
using Xunit;

namespace Trellis.Application.UnitTests.Auth;

/// <summary>
/// Conventions over every validator <c>AddApplication</c> registers. A command that carries a string carries
/// caller-controlled text, so it gets a validator that bounds it: without one a null or a megabyte string
/// reaches the handler and its ports (the auth commands answered 500 for a missing token until this test
/// existed). And every <c>Must</c> rule names its code. Both live with the auth tests because those commands
/// prompted the first; they cover every command in the assembly.
/// </summary>
public sealed class CommandValidatorConventionTests
{
    [Fact]
    public void EveryCommandWithStringInput_HasARegisteredValidator()
    {
        var services = new ServiceCollection();
        services.AddApplication();

        Type[] commandsWithText =
        [
            .. typeof(DependencyInjection).Assembly.GetTypes()
                .Where(static type => type is { IsClass: true, IsAbstract: false } && IsCommand(type))
                .Where(static type => type.GetProperties().Any(static property => property.PropertyType == typeof(string))),
        ];

        // Guards against a scan that silently finds nothing.
        commandsWithText.ShouldContain(typeof(LoginCommand));

        string[] missing =
        [
            .. commandsWithText
                .Where(command => !services.Any(descriptor =>
                    descriptor.ServiceType == typeof(IValidator<>).MakeGenericType(command)))
                .Select(static command => command.FullName!),
        ];

        missing.ShouldBeEmpty(
            "every ICommand with a string property needs an AbstractValidator<T> next to it "
            + "(NotEmpty and a MaximumLength at least)");
    }

    /// <summary>
    /// FluentValidation names an uncoded <c>Must</c> after its predicate validator, so it reaches the wire as
    /// <c>Validation.Predicate</c> (<c>Validation.AsyncPredicate</c> for <c>MustAsync</c>): a code no client can
    /// branch on, shared by every such rule. The rules themselves are read, not the source, so a code set
    /// through a helper or <c>Configure</c> counts.
    /// </summary>
    [Fact]
    public void EveryMustRule_NamesItsErrorCode()
    {
        var services = new ServiceCollection();
        services.AddApplication();

        Type[] validators =
        [
            .. services
                .Where(static descriptor => descriptor.ServiceType.IsGenericType
                                            && descriptor.ServiceType.GetGenericTypeDefinition() == typeof(IValidator<>))
                .Select(static descriptor => descriptor.ImplementationType)
                .OfType<Type>()
                .Distinct(),
        ];

        // Guards against a scan that silently finds nothing.
        validators.ShouldContain(typeof(LoginCommandValidator));

        string[] uncoded = [.. validators.SelectMany(static validator => UncodedPredicates(Build(validator)))];

        uncoded.ShouldBeEmpty("give every .Must(...) and .MustAsync(...) a .WithErrorCode(\"Area.Problem\") (sharedkernel.md)");
    }

    [Fact]
    public void TheMustRuleCheck_FlagsOnlyTheUncodedPredicates()
        => UncodedPredicates(new PlantedValidator()).ShouldBe(
        [
            "PlantedValidator.Name: PredicateValidator",
            "PlantedValidator.Name: AsyncPredicateValidator",
            "PlantedValidator.(whole request): PredicateValidator",
        ]);

    private static bool IsCommand(Type type) => type.GetInterfaces().Any(static contract =>
        contract == typeof(ICommand)
        || (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(ICommand<>)));

    /// <summary>
    /// A validator builds its rules in its constructor; a dependency (a repository behind a <c>MustAsync</c>,
    /// say) is only called when a rule runs, so a substitute is enough to read the rules.
    /// </summary>
    private static IValidator Build(Type validator)
    {
        ConstructorInfo constructor = validator
            .GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .OrderByDescending(static candidate => candidate.GetParameters().Length)
            .First();

        object[] dependencies = [.. constructor.GetParameters().Select(static parameter => Substitute.For([parameter.ParameterType], []))];
        return (IValidator)constructor.Invoke(dependencies);
    }

    private static IEnumerable<string> UncodedPredicates(IValidator validator)
        => validator is IEnumerable<IValidationRule> rules
            ? rules.SelectMany(rule => rule.Components
                .Where(static component => component.Validator.Name is "PredicateValidator" or "AsyncPredicateValidator"
                                           && string.IsNullOrEmpty(component.ErrorCode))
                .Select(component =>
                    $"{validator.GetType().Name}.{(string.IsNullOrEmpty(rule.PropertyName) ? "(whole request)" : rule.PropertyName)}: {component.Validator.Name}"))
            : [];

    private sealed record PlantedRequest(string Name);

    /// <summary>The positive control: two uncoded predicates on a member, one on the whole request, one coded.</summary>
    private sealed class PlantedValidator : AbstractValidator<PlantedRequest>
    {
        public PlantedValidator()
        {
            RuleFor(static request => request.Name).Must(static name => name.Length > 1);
            RuleFor(static request => request.Name).MustAsync(static (name, _) => Task.FromResult(name.Length < 100));
            RuleFor(static request => request.Name).Must(static name => name.Trim() == name).WithErrorCode("Planted.Untrimmed");
            RuleFor(static request => request).Must(static request => request.Name.Length > 0);
        }
    }
}
