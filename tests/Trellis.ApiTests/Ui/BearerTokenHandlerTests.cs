#if (IncludeUI)
using System.Net;
using Shouldly;
using Trellis.Api.Contracts.Auth;
using Xunit;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// The bearer chain AddSharedUi builds: invoice calls carry the circuit's currently-valid access token
/// (rotated first when it is about to expire); the anonymous auth calls carry none.
/// </summary>
public sealed class BearerTokenHandlerTests
{
    [Fact]
    public async Task Invoice_calls_carry_the_current_access_token()
    {
        await using var ui = new UiHarness();
        string token = TestJwt.ExpiringAt(ui.Clock.GetUtcNow().AddMinutes(10));
        await ui.Tokens.SetTokensAsync(token, "refresh-1");
        ui.Api.Always(static _ => Responses.Status(HttpStatusCode.NoContent));

        (await ui.InvoiceApi.PayAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        RecordedRequest call = ui.Api.Requests.ShouldHaveSingleItem();
        call.Authorization.ShouldBe($"Bearer {token}");
    }

    [Fact]
    public async Task An_expiring_token_is_rotated_before_the_call_goes_out()
    {
        await using var ui = new UiHarness();
        await ui.Tokens.SetTokensAsync(TestJwt.ExpiringAt(ui.Clock.GetUtcNow().AddSeconds(10)), "refresh-1");
        string rotated = TestJwt.ExpiringAt(ui.Clock.GetUtcNow().AddMinutes(15));
        ui.Api.Always(request => request.RequestUri!.AbsolutePath.EndsWith("/auth/refresh", StringComparison.Ordinal)
            ? Responses.Json(new TokenResponse(rotated, "refresh-2", 900))
            : Responses.Status(HttpStatusCode.NoContent));

        await ui.InvoiceApi.PayAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken);

        ui.Api.Requests.Select(static r => (r.Path.EndsWith("/auth/refresh", StringComparison.Ordinal), r.Authorization))
            .ShouldBe([(true, null), (false, $"Bearer {rotated}")]);
    }

    [Fact]
    public async Task Without_a_session_no_authorization_header_is_sent()
    {
        await using var ui = new UiHarness();
        ui.Api.Always(static _ => Responses.Status(HttpStatusCode.Unauthorized));

        await ui.InvoiceApi.PayAsync(Guid.CreateVersion7(), TestContext.Current.CancellationToken);

        ui.Api.Requests.ShouldHaveSingleItem().Authorization.ShouldBeNull();
    }

    [Fact]
    public async Task Auth_calls_never_carry_a_bearer_token()
    {
        await using var ui = new UiHarness();
        await ui.Tokens.SetTokensAsync(TestJwt.ExpiringAt(ui.Clock.GetUtcNow().AddMinutes(10)), "refresh-1");
        ui.Api.Always(static _ => Responses.Json(new TokenResponse("a", "r", 900)));

        await ui.AuthApi.LoginAsync(new LoginRequest("someone@example.com", "password"), TestContext.Current.CancellationToken);

        ui.Api.Requests.ShouldHaveSingleItem().Authorization.ShouldBeNull();
    }
}
#endif
