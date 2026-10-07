#if (IncludeUI)
using System.Net;
using Microsoft.AspNetCore.Components.Authorization;
using Shouldly;
using Trellis.Api.Contracts.Auth;
using Xunit;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// Session lifetime in the UI (ADR-0021 rule 3): only a 401 from the refresh endpoint ends a session.
/// A 429, a 5xx or an unreachable API keeps the stored pair, holds off the next attempt for 5 s and hands
/// back the stored token, so a protected page does not bounce to sign-in. Rotation stays single-flight
/// and starts 30 s before expiry.
/// </summary>
public sealed class TokenAuthenticationStateProviderTests
{
    private const string RefreshPath = "/api/v1/auth/refresh";
    private const string RefreshToken = "refresh-1";

    private static readonly TimeSpan HoldOff = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Refresh_answered_429_keeps_the_tokens_and_holds_off_for_five_seconds()
    {
        await using var ui = new UiHarness();
        string expired = await SignedInWithExpiredTokenAsync(ui);
        ui.Api.Always(static _ => Responses.Problem(
            HttpStatusCode.TooManyRequests,
            """{"title":"Too many requests","status":429}""",
            retryAfterSeconds: 30));
        StateChangeCounter stateChanges = new(ui.AuthState);

        (await ui.AuthState.GetValidAccessTokenAsync(TestContext.Current.CancellationToken)).ShouldBe(expired);
        ui.Api.CountTo(RefreshPath).ShouldBe(1);
        (await ui.Tokens.GetAccessTokenAsync()).ShouldBe(expired);
        (await ui.Tokens.GetRefreshTokenAsync()).ShouldBe(RefreshToken);

        // Inside the hold-off: no second refresh, the stored token comes back, the user stays signed in.
        ui.Clock.Advance(HoldOff - TimeSpan.FromMilliseconds(1));
        (await ui.AuthState.GetValidAccessTokenAsync(TestContext.Current.CancellationToken)).ShouldBe(expired);
        AuthenticationState state = await ui.AuthState.GetAuthenticationStateAsync();
        state.User.Identity?.IsAuthenticated.ShouldBe(true);
        ui.Api.CountTo(RefreshPath).ShouldBe(1);

        // Once it has passed, the next call tries again.
        ui.Clock.Advance(TimeSpan.FromMilliseconds(1));
        await ui.AuthState.GetValidAccessTokenAsync(TestContext.Current.CancellationToken);
        ui.Api.CountTo(RefreshPath).ShouldBe(2);

        stateChanges.Count.ShouldBe(0);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Transient_refresh_failure_is_not_a_sign_out(HttpStatusCode status)
    {
        await using var ui = new UiHarness();
        string expired = await SignedInWithExpiredTokenAsync(ui);
        ui.Api.Always(_ => Responses.Status(status));
        StateChangeCounter stateChanges = new(ui.AuthState);

        string? token = await ui.AuthState.GetValidAccessTokenAsync(TestContext.Current.CancellationToken);

        token.ShouldBe(expired);
        (await ui.Tokens.GetRefreshTokenAsync()).ShouldBe(RefreshToken);
        stateChanges.Count.ShouldBe(0);
    }

    [Fact]
    public async Task Unreachable_refresh_endpoint_is_not_a_sign_out()
    {
        await using var ui = new UiHarness();
        string expired = await SignedInWithExpiredTokenAsync(ui);
        ui.Api.Respond = static (_, _) => throw new HttpRequestException("Connection refused");

        (await ui.AuthState.GetValidAccessTokenAsync(TestContext.Current.CancellationToken)).ShouldBe(expired);
        (await ui.Tokens.GetRefreshTokenAsync()).ShouldBe(RefreshToken);
    }

    [Fact]
    public async Task Refresh_answered_401_ends_the_session()
    {
        await using var ui = new UiHarness();
        await SignedInWithExpiredTokenAsync(ui);
        ui.Api.Always(static _ => Responses.Problem(
            HttpStatusCode.Unauthorized,
            """{"title":"Auth.InvalidRefreshToken","status":401,"detail":"The refresh token is invalid or has expired."}"""));
        StateChangeCounter stateChanges = new(ui.AuthState);

        string? token = await ui.AuthState.GetValidAccessTokenAsync(TestContext.Current.CancellationToken);

        token.ShouldBeNull();
        (await ui.Tokens.GetAccessTokenAsync()).ShouldBeNull();
        (await ui.Tokens.GetRefreshTokenAsync()).ShouldBeNull();
        stateChanges.Count.ShouldBe(1);
        (await ui.AuthState.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated.ShouldBe(false);
    }

    [Fact]
    public async Task Refresh_after_the_hold_off_stores_the_rotated_pair()
    {
        await using var ui = new UiHarness();
        await SignedInWithExpiredTokenAsync(ui);
        ui.Api.Always(static _ => Responses.Status(HttpStatusCode.ServiceUnavailable));
        await ui.AuthState.GetValidAccessTokenAsync(TestContext.Current.CancellationToken);

        ui.Clock.Advance(HoldOff);
        string rotated = TestJwt.ExpiringAt(ui.Clock.GetUtcNow().AddMinutes(15));
        ui.Api.Always(_ => Responses.Json(new TokenResponse(rotated, "refresh-2", 900)));

        (await ui.AuthState.GetValidAccessTokenAsync(TestContext.Current.CancellationToken)).ShouldBe(rotated);
        (await ui.Tokens.GetRefreshTokenAsync()).ShouldBe("refresh-2");
    }

    [Fact]
    public async Task Concurrent_callers_share_one_refresh()
    {
        await using var ui = new UiHarness();
        await SignedInWithExpiredTokenAsync(ui);
        string rotated = TestJwt.ExpiringAt(ui.Clock.GetUtcNow().AddMinutes(15));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Api.Respond = async (_, cancellationToken) =>
        {
            await release.Task.WaitAsync(cancellationToken);
            return Responses.Json(new TokenResponse(rotated, "refresh-2", 900));
        };

        Task<string?> first = ui.AuthState.GetValidAccessTokenAsync(TestContext.Current.CancellationToken).AsTask();
        Task<string?> second = ui.AuthState.GetValidAccessTokenAsync(TestContext.Current.CancellationToken).AsTask();
        release.SetResult();

        (await Task.WhenAll(first, second)).ShouldAllBe(token => token == rotated);
        ui.Api.CountTo(RefreshPath).ShouldBe(1);
    }

    [Theory]
    [InlineData(20, true)]    // inside the 30 s skew: rotated before it dies in flight
    [InlineData(60, false)]   // comfortably valid: used as is
    public async Task Rotation_starts_thirty_seconds_before_expiry(int secondsLeft, bool refreshes)
    {
        await using var ui = new UiHarness();
        await ui.Tokens.SetTokensAsync(TestJwt.ExpiringAt(ui.Clock.GetUtcNow().AddSeconds(secondsLeft)), RefreshToken);
        ui.Api.Always(_ => Responses.Json(new TokenResponse(
            TestJwt.ExpiringAt(ui.Clock.GetUtcNow().AddMinutes(15)), "refresh-2", 900)));

        await ui.AuthState.GetValidAccessTokenAsync(TestContext.Current.CancellationToken);

        ui.Api.CountTo(RefreshPath).ShouldBe(refreshes ? 1 : 0);
    }

    /// <summary>A signed-in circuit whose access token expired a minute ago; returns that token.</summary>
    private static async Task<string> SignedInWithExpiredTokenAsync(UiHarness ui)
    {
        string expired = TestJwt.ExpiringAt(ui.Clock.GetUtcNow().AddMinutes(-1));
        await ui.Tokens.SetTokensAsync(expired, RefreshToken);
        return expired;
    }

    /// <summary>Counts the provider's AuthenticationStateChanged notifications from construction on.</summary>
    private sealed class StateChangeCounter
    {
        private int _count;

        public StateChangeCounter(AuthenticationStateProvider provider)
            => provider.AuthenticationStateChanged += _ => Interlocked.Increment(ref _count);

        public int Count => Volatile.Read(ref _count);
    }
}
#endif
