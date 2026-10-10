using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Application.Features.Auth.Login;
using Trellis.SharedKernel.Messaging;
using Xunit;

namespace Trellis.Application.UnitTests.Auth;

/// <summary>
/// Convention: a command that carries a string carries caller-controlled text, so it gets a
/// validator that bounds it. Without one a null or a megabyte string reaches the handler and its
/// ports (the auth commands answered 500 for a missing token until this test existed). It lives with
/// the auth tests because those commands prompted it; it covers every command in the assembly.
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

    private static bool IsCommand(Type type) => type.GetInterfaces().Any(static contract =>
        contract == typeof(ICommand)
        || (contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(ICommand<>)));
}
