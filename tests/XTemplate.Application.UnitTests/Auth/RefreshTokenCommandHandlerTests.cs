using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using XTemplate.Application.Abstractions;
using XTemplate.Application.Features.Auth;
using XTemplate.Application.Features.Auth.RefreshToken;
using XTemplate.Application.Options;
using XTemplate.SharedKernel.Results;
using Xunit;

namespace XTemplate.Application.UnitTests.Auth;

public sealed class RefreshTokenCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedUser User = new(Guid.CreateVersion7(), "member@acme.com", ["invoices:read"]);

    private readonly IIdentityService _identity = Substitute.For<IIdentityService>();
    private readonly ITokenProvider _tokens = Substitute.For<ITokenProvider>();
    private readonly IRefreshTokenStore _refreshStore = Substitute.For<IRefreshTokenStore>();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly AuthTokenOptions _options = new();

    private RefreshTokenCommandHandler CreateHandler() => new(_identity, _tokens, _refreshStore, _time, _options);

    [Fact]
    public async Task Valid_token_is_consumed_and_a_fresh_pair_is_issued()
    {
        _tokens.HashRefreshToken("presented").Returns("hashed");
        _refreshStore.ConsumeAsync("hashed", Arg.Any<CancellationToken>()).Returns(Result.Success(User.Id));
        _identity.GetUserAsync(User.Id, Arg.Any<CancellationToken>()).Returns(Result.Success(User));
        _tokens.CreateAccessToken(User).Returns("new-access");
        _tokens.CreateRefreshToken().Returns("new-refresh");
        _tokens.HashRefreshToken("new-refresh").Returns("new-hashed");

        var result = await CreateHandler().Handle(
            new RefreshTokenCommand("presented"), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.AccessToken.ShouldBe("new-access");
        await _refreshStore.Received(1).ConsumeAsync("hashed", Arg.Any<CancellationToken>());
        await _refreshStore.Received(1).StoreAsync(User.Id, "new-hashed", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalid_or_consumed_token_is_rejected()
    {
        _tokens.HashRefreshToken(Arg.Any<string>()).Returns("hashed");
        _refreshStore.ConsumeAsync("hashed", Arg.Any<CancellationToken>())
            .Returns(Result.Failure<Guid>(AuthErrors.InvalidRefreshToken));

        var result = await CreateHandler().Handle(
            new RefreshTokenCommand("stale"), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(AuthErrors.InvalidRefreshToken);
        await _identity.DidNotReceiveWithAnyArgs().GetUserAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _refreshStore.DidNotReceiveWithAnyArgs().StoreAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }
}
