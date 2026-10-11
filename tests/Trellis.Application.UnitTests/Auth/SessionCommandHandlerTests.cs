using NSubstitute;
using Shouldly;
using Trellis.Application.Abstractions;
using Trellis.Application.Features.Auth;
using Trellis.Application.Features.Auth.Logout;
using Trellis.Application.Features.Auth.RevokeSessions;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.Application.UnitTests.Auth;

/// <summary>Logout and revoke-all: the two ways a session ends on purpose.</summary>
public sealed class SessionCommandHandlerTests
{
    private readonly ITokenProvider _tokens = Substitute.For<ITokenProvider>();
    private readonly IRefreshTokenStore _refreshStore = Substitute.For<IRefreshTokenStore>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    [Fact]
    public async Task Logout_RevokesThePresentedTokenByHashWithoutConsumingIt()
    {
        _tokens.HashRefreshToken("presented").Returns("hashed");

        Result result = await new LogoutCommandHandler(_tokens, _refreshStore)
            .Handle(new LogoutCommand("presented"), TestContext.Current.CancellationToken);

        result.ShouldBeSuccess();
        await _refreshStore.Received(1).RevokeAsync("hashed", Arg.Any<CancellationToken>());
        // Consuming is what replay detection keys on; a logout must never look like a replay.
        await _refreshStore.DidNotReceiveWithAnyArgs().ConsumeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeSessions_RevokesEveryTokenOfTheCaller()
    {
        Guid userId = Guid.CreateVersion7();
        _currentUser.UserId.Returns(userId);

        Result result = await new RevokeSessionsCommandHandler(_currentUser, _refreshStore)
            .Handle(new RevokeSessionsCommand(), TestContext.Current.CancellationToken);

        result.ShouldBeSuccess();
        await _refreshStore.Received(1).RevokeAllAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RevokeSessions_WithoutAnAuthenticatedCaller_FailsAndRevokesNothing()
    {
        _currentUser.UserId.Returns((Guid?)null);

        Result result = await new RevokeSessionsCommandHandler(_currentUser, _refreshStore)
            .Handle(new RevokeSessionsCommand(), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(AuthErrors.InvalidCredentials);
        await _refreshStore.DidNotReceiveWithAnyArgs().RevokeAllAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
