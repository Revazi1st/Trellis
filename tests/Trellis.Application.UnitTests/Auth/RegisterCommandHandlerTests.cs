using NSubstitute;
using Shouldly;
using Trellis.Application.Abstractions;
using Trellis.Application.Features.Auth;
using Trellis.Application.Features.Auth.Register;
using Trellis.Application.Options;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.Application.UnitTests.Auth;

public sealed class RegisterCommandHandlerTests
{
    private static readonly RegisterCommand Command = new("new@acme.com", "Sup3r-secret!");

    private readonly IIdentityService _identity = Substitute.For<IIdentityService>();

    [Fact]
    public async Task Handle_RegistrationClosed_FailsWithRegistrationClosedAndCreatesNothing()
    {
        var handler = new RegisterCommandHandler(_identity, new RegistrationOptions { Mode = RegistrationMode.Closed });

        Result<Guid> result = await handler.Handle(Command, TestContext.Current.CancellationToken);

        result.Error.ShouldBe(AuthErrors.RegistrationClosed);
        result.Error.Type.ShouldBe(ErrorType.Forbidden);
        await _identity.DidNotReceiveWithAnyArgs().RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_DefaultOptions_AreClosed()
    {
        var handler = new RegisterCommandHandler(_identity, new RegistrationOptions());

        Result<Guid> result = await handler.Handle(Command, TestContext.Current.CancellationToken);

        result.Error.ShouldBe(AuthErrors.RegistrationClosed);
        await _identity.DidNotReceiveWithAnyArgs().RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_RegistrationOpen_CreatesTheAccountThroughTheIdentityPort()
    {
        Guid userId = Guid.CreateVersion7();
        _identity.RegisterAsync(Command.Email, Command.Password, Arg.Any<CancellationToken>())
            .Returns(Result.Success(userId));
        var handler = new RegisterCommandHandler(_identity, new RegistrationOptions { Mode = RegistrationMode.Open });

        Result<Guid> result = await handler.Handle(Command, TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.ShouldBe(userId);
        await _identity.Received(1).RegisterAsync(Command.Email, Command.Password, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_RegistrationOpen_PassesIdentityFailuresThrough()
    {
        _identity.RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>(AuthErrors.EmailAlreadyInUse));
        var handler = new RegisterCommandHandler(_identity, new RegistrationOptions { Mode = RegistrationMode.Open });

        Result<Guid> result = await handler.Handle(Command, TestContext.Current.CancellationToken);

        result.Error.ShouldBe(AuthErrors.EmailAlreadyInUse);
    }
}
