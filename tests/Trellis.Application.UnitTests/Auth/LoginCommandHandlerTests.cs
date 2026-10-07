using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Trellis.Application.Abstractions;
using Trellis.Application.Features.Auth;
using Trellis.Application.Features.Auth.Login;
using Trellis.Application.Options;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.Application.UnitTests.Auth;

public sealed class LoginCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly AuthenticatedUser User = new(
        Guid.CreateVersion7(), "member@acme.com", ["invoices:read"]
#if (UseMultitenancy)
        , "tenant-a"
#endif
        );

    private readonly IIdentityService _identity = Substitute.For<IIdentityService>();
    private readonly ITokenProvider _tokens = Substitute.For<ITokenProvider>();
    private readonly IRefreshTokenStore _refreshStore = Substitute.For<IRefreshTokenStore>();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly AuthTokenOptions _options = new() { AccessTokenMinutes = 15, RefreshTokenDays = 7 };

    private LoginCommandHandler CreateHandler() => new(_identity, _tokens, _refreshStore, _time, _options);

    [Fact]
    public async Task Valid_credentials_mint_tokens_and_store_hashed_refresh_token()
    {
        _identity.ValidateCredentialsAsync("member@acme.com", "pw", Arg.Any<CancellationToken>())
            .Returns(Result.Success(User));
        _tokens.CreateAccessToken(User).Returns("access-token");
        _tokens.CreateRefreshToken().Returns("refresh-token");
        _tokens.HashRefreshToken("refresh-token").Returns("hashed");

        var result = await CreateHandler().Handle(
            new LoginCommand("member@acme.com", "pw"), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.AccessToken.ShouldBe("access-token");
        result.Value.RefreshToken.ShouldBe("refresh-token");
        result.Value.ExpiresInSeconds.ShouldBe(15 * 60);
        await _refreshStore.Received(1).StoreAsync(
            User.Id, "hashed", Now.AddDays(7), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalid_credentials_fail_without_minting_or_storing_anything()
    {
        _identity.ValidateCredentialsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Result.Failure<AuthenticatedUser>(AuthErrors.InvalidCredentials));

        var result = await CreateHandler().Handle(
            new LoginCommand("member@acme.com", "wrong"), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(AuthErrors.InvalidCredentials);
        _tokens.DidNotReceiveWithAnyArgs().CreateAccessToken(default!);
        await _refreshStore.DidNotReceiveWithAnyArgs().StoreAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }
}
