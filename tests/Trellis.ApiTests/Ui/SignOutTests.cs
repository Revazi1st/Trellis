#if (IncludeUI)
using System.Net;
using Microsoft.AspNetCore.Components.Authorization;
using Shouldly;
using Trellis.Api.Contracts.Auth;
using Xunit;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// Ending a session in the UI: sign-out forgets the tokens and announces it at once, then revokes the
/// refresh token at the API (<c>POST /auth/logout</c>, best-effort); a closing circuit revokes without
/// announcing; a rotation in flight finishes first, so the token revoked is the current one and no
/// rotated pair is stored after the sign-out.
/// </summary>
public sealed class SignOutTests
{
    private const string LogoutPath = "/api/v1/auth/logout";
    private const string RefreshPath = "/api/v1/auth/refresh";

    [Fact]
    public async Task Sign_out_forgets_the_tokens_announces_it_and_revokes_the_refresh_token()
    {
        await using var ui = new UiHarness();
        await ui.Tokens.SetTokensAsync(TestJwt.ExpiringAt(ui.Clock.GetUtcNow().AddMinutes(10)), "refresh-1");
        ui.Api.Always(static _ => Responses.Status(HttpStatusCode.NoContent));
        var announced = new List<Task<AuthenticationState>>();
        ui.AuthState.AuthenticationStateChanged += announced.Add;

        await ui.AuthState.SignOutAsync(TestContext.Current.CancellationToken);

        (await ui.Tokens.GetAccessTokenAsync()).ShouldBeNull();
        (await ui.Tokens.GetRefreshTokenAsync()).ShouldBeNull();
        (await Task.WhenAll(announced)).Select(static state => state.User.Identity?.IsAuthenticated == true).ShouldBe([false]);
        RecordedRequest logout = ui.Api.Requests.ShouldHaveSingleItem();
        (logout.Method, logout.Path, logout.Authorization).ShouldBe(("POST", LogoutPath, null));
        logout.Body.ShouldBe("""{"refreshToken":"refresh-1"}""");
    }

    [Theory]
    [InlineData("unreachable")]
    [InlineData("server error")]
    [InlineData("rate limited")]
    public async Task Sign_out_is_local_even_when_the_revoke_fails(string failure)
    {
        await using var ui = new UiHarness();
        await ui.Tokens.SetTokensAsync(TestJwt.ExpiringAt(ui.Clock.GetUtcNow().AddMinutes(10)), "refresh-1");
        ui.Api.Respond = failure switch
        {
            "unreachable" => static (_, _) => throw new HttpRequestException("Connection refused"),
            "server error" => static (_, _) => Task.FromResult(Responses.Status(HttpStatusCode.InternalServerError)),
            _ => static (_, _) => Task.FromResult(Responses.Problem(
                HttpStatusCode.TooManyRequests, """{"code":"RateLimit.Exceeded","status":429}""", retryAfterSeconds: 30)),
        };

        await ui.AuthState.SignOutAsync(TestContext.Current.CancellationToken);

        (await ui.Tokens.GetRefreshTokenAsync()).ShouldBeNull();
        (await ui.AuthState.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated.ShouldBe(false);
        ui.Api.CountTo(LogoutPath).ShouldBe(1);
    }

    [Fact]
    public async Task Sign_out_without_a_session_calls_nothing()
    {
        await using var ui = new UiHarness();

        await ui.AuthState.SignOutAsync(TestContext.Current.CancellationToken);

        ui.Api.Requests.ShouldBeEmpty();
    }

    [Fact]
    public async Task Closing_the_session_revokes_without_announcing()
    {
        await using var ui = new UiHarness();
        await ui.Tokens.SetTokensAsync(TestJwt.ExpiringAt(ui.Clock.GetUtcNow().AddMinutes(10)), "refresh-1");
        ui.Api.Always(static _ => Responses.Status(HttpStatusCode.NoContent));
        int announcements = 0;
        ui.AuthState.AuthenticationStateChanged += _ => announcements++;

        await ui.AuthState.CloseSessionAsync(TestContext.Current.CancellationToken);

        (await ui.Tokens.GetRefreshTokenAsync()).ShouldBeNull();
        announcements.ShouldBe(0);
        ui.Api.Requests.ShouldHaveSingleItem().Body.ShouldBe("""{"refreshToken":"refresh-1"}""");
    }

    [Fact]
    public async Task Sign_out_during_a_rotation_revokes_the_rotated_token_and_stays_signed_out()
    {
        await using var ui = new UiHarness();
        await ui.Tokens.SetTokensAsync(TestJwt.ExpiringAt(ui.Clock.GetUtcNow().AddMinutes(-1)), "refresh-1");
        string rotated = TestJwt.ExpiringAt(ui.Clock.GetUtcNow().AddMinutes(15));
        var refreshArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRefresh = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ui.Api.Respond = async (request, cancellationToken) =>
        {
            if (request.RequestUri!.AbsolutePath == RefreshPath)
            {
                refreshArrived.SetResult();
                await releaseRefresh.Task.WaitAsync(cancellationToken);
                return Responses.Json(new TokenResponse(rotated, "refresh-2", 900));
            }

            return Responses.Status(HttpStatusCode.NoContent);
        };

        Task<string?> rotation = ui.AuthState.GetValidAccessTokenAsync(TestContext.Current.CancellationToken).AsTask();
        await refreshArrived.Task.WaitAsync(TestContext.Current.CancellationToken);
        Task signOut = ui.AuthState.SignOutAsync(TestContext.Current.CancellationToken);
        releaseRefresh.SetResult();
        await Task.WhenAll(rotation, signOut);

        ui.Api.Requests.Where(static r => r.Path == LogoutPath).ShouldHaveSingleItem()
            .Body.ShouldBe("""{"refreshToken":"refresh-2"}""");
        (await ui.Tokens.GetAccessTokenAsync()).ShouldBeNull();
        (await ui.Tokens.GetRefreshTokenAsync()).ShouldBeNull();
        AuthenticationState state = await ui.AuthState.GetAuthenticationStateAsync();
        state.User.Identity?.IsAuthenticated.ShouldBe(false);
    }
}
#endif
