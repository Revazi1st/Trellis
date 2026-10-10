using System.Net;
using System.Net.Http.Json;
using Shouldly;
using Trellis.Api.Common.Http;
using Trellis.Api.Contracts.Auth;
using Trellis.Api.Contracts.Common;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// The rate-limiting contract (ADR-0021): the strict <c>auth</c> policy guards the endpoints that
/// accept a guessable credential, token endpoints and probes are not starved by it or by the global
/// limiter. Its own factory, because these tests deliberately exhaust partitions.
/// </summary>
public sealed class AuthRateLimitTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    // A web host (Blazor Server) relays every user's calls from one socket: one address, many sessions.
    private static readonly IPAddress SharedWebHost = IPAddress.Parse("198.51.100.10");

    [Fact]
    public async Task Auth_endpoints_throttle_after_the_policy_limit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = factory.CreateClient();

        // The "auth" policy allows 10/min per partition; the 11th+ call inside the window must 429.
        var responses = new List<HttpResponseMessage>();
        for (int attempt = 0; attempt < 12; attempt++)
        {
            responses.Add(await client.PostAsJsonAsync(
                "/api/v1/auth/login", new LoginRequest("nobody@nowhere.dev", "wrong-password"), ct));
        }

        responses.Take(10).ShouldAllBe(static response => response.StatusCode == HttpStatusCode.Unauthorized);

        HttpResponseMessage throttled = responses.Skip(10).First();
        throttled.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter.ShouldNotBeNull().Delta.ShouldNotBeNull().ShouldBeGreaterThan(TimeSpan.Zero);
        throttled.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");

        // The limiter's rejection is written through IProblemDetailsService like every other problem (ADR-0024).
        ProblemResponse problem = (await throttled.Content.ReadFromJsonAsync<ProblemResponse>(ct)).ShouldNotBeNull();
        problem.Status.ShouldBe(429);
        problem.Code.ShouldBe("RateLimit.Exceeded");
        problem.Type.ShouldBe(ProblemTypes.For("RateLimit.Exceeded"));
        problem.TraceId.ShouldNotBeNullOrWhiteSpace();
        problem.CorrelationId.ShouldBe(throttled.Headers.GetValues(CorrelationIdMiddleware.HeaderName).Single());
    }

    [Fact]
    public async Task Refresh_is_not_throttled_by_the_auth_policy()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = factory.CreateClient();

        HttpResponseMessage login = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new LoginRequest(ApiFactory.AdminEmail, ApiFactory.AdminPassword), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        TokenResponse tokens = (await login.Content.ReadFromJsonAsync<TokenResponse>(ct))!;

        // One login + 12 chained rotations from ONE source is 13 calls: past the auth policy's 10/min,
        // well inside the global limiter's 100/min.
        for (int rotation = 1; rotation <= 12; rotation++)
        {
            HttpResponseMessage refreshed = await client.PostAsJsonAsync(
                "/api/v1/auth/refresh", new RefreshRequest(tokens.RefreshToken), ct);

            refreshed.StatusCode.ShouldBe(HttpStatusCode.OK, $"rotation {rotation}");
            tokens = (await refreshed.Content.ReadFromJsonAsync<TokenResponse>(ct))!;
        }
    }

    [Fact]
    public async Task Twelve_sessions_refreshing_through_one_web_host_within_a_minute_all_succeed()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Twelve users signed in (from their own browsers' addresses)...
        var refreshTokens = new List<string>();
        for (int user = 0; user < 12; user++)
        {
            refreshTokens.Add((await factory.LoginAsAdminAsync(ct)).RefreshToken);
        }

        // ...and the web host silently refreshes every one of their sessions from its single address.
        HttpClient webHost = factory.CreateClientFrom(SharedWebHost);
        var statuses = new List<HttpStatusCode>();
        foreach (string refreshToken in refreshTokens)
        {
            HttpResponseMessage response = await webHost.PostAsJsonAsync(
                "/api/v1/auth/refresh", new RefreshRequest(refreshToken), ct);
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldAllBe(static status => status == HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public async Task Probes_are_exempt_from_the_global_rate_limiter(string probe)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient orchestrator = factory.CreateClient();

        // The global limiter allows 100/min per source; an orchestrator probing past that must never see 429.
        var statuses = new List<HttpStatusCode>();
        for (int call = 0; call < 101; call++)
        {
            statuses.Add((await orchestrator.GetAsync(probe, ct)).StatusCode);
        }

        statuses.ShouldAllBe(static status => status == HttpStatusCode.OK);
    }
}
