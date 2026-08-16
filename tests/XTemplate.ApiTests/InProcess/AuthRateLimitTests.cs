using System.Net;
using System.Net.Http.Json;
using Shouldly;
using XTemplate.Api.Contracts.Auth;
using Xunit;

namespace XTemplate.ApiTests.InProcess;

/// <summary>
/// Isolated in its own factory: hammering the auth limiter would poison the shared partition for
/// every other test in a common host.
/// </summary>
public sealed class AuthRateLimitTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Auth_endpoints_throttle_after_the_policy_limit()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = factory.CreateClient();

        // The "auth" policy allows 10/min per partition; the 11th+ call inside the window must 429.
        var statuses = new List<HttpStatusCode>();
        for (int attempt = 0; attempt < 12; attempt++)
        {
            HttpResponseMessage response = await client.PostAsJsonAsync(
                "/api/v1/auth/login", new LoginRequest("nobody@nowhere.dev", "wrong-password"), ct);
            statuses.Add(response.StatusCode);
        }

        statuses.ShouldContain(HttpStatusCode.TooManyRequests);
        statuses.Take(10).ShouldAllBe(static status => status == HttpStatusCode.Unauthorized);
    }
}
