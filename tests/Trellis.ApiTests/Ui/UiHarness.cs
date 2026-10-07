#if (IncludeUI)
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Trellis.UI.Common;
using Trellis.UI.Common.Auth;
using Trellis.UI.Common.Services;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// UI.Common's real service graph (<c>AddSharedUi</c>: typed clients, bearer handler, token provider)
/// with the network swapped for <see cref="ScriptedApi"/> and the clock for <see cref="ManualClock"/>.
/// One harness is one Blazor circuit (one DI scope). No browser, no host, no API.
/// </summary>
internal sealed class UiHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly ServiceProvider _root;
    private readonly AsyncServiceScope _circuit;

    public UiHarness(TimeSpan? clientTimeout = null)
    {
        var services = new ServiceCollection();
        // Every named client's primary handler, so the test needs no knowledge of the client's name.
        services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => Api));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddScoped<ITokenStore, InMemoryTokenStore>();
        services.AddSharedUi((_, client) =>
        {
            client.BaseAddress = new Uri("http://api.test/");
            if (clientTimeout is { } timeout)
            {
                client.Timeout = timeout;
            }
        });

        _root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _circuit = _root.CreateAsyncScope();
    }

    public ScriptedApi Api { get; } = new();

    public ManualClock Clock { get; } = new(Start);

    public IAuthApiClient AuthApi => Get<IAuthApiClient>();

    public IInvoiceApiClient InvoiceApi => Get<IInvoiceApiClient>();

    public TokenAuthenticationStateProvider AuthState => Get<TokenAuthenticationStateProvider>();

    public ITokenStore Tokens => Get<ITokenStore>();

    public async ValueTask DisposeAsync()
    {
        await _circuit.DisposeAsync();
        await _root.DisposeAsync();
        Api.Dispose();
    }

    private T Get<T>() where T : notnull => _circuit.ServiceProvider.GetRequiredService<T>();
}

/// <summary>Stands in for the API: records each request and answers it with <see cref="Respond"/>.</summary>
internal sealed class ScriptedApi : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = [];

    /// <summary>Answers every request; the default is a bodyless 404.</summary>
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; }
        = static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public int CountTo(string path) => Requests.Count(request => string.Equals(request.Path, path, StringComparison.Ordinal));

    /// <summary>Answers every request with <paramref name="respond"/>.</summary>
    public void Always(Func<HttpRequestMessage, HttpResponseMessage> respond)
        => Respond = (request, _) => Task.FromResult(respond(request));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        lock (_requests)
        {
            _requests.Add(new RecordedRequest(
                request.Method.Method,
                request.RequestUri!.AbsolutePath,
                request.Headers.Authorization?.ToString()));
        }

        HttpResponseMessage response = await Respond(request, cancellationToken);
        response.RequestMessage = request;
        return response;
    }
}

/// <summary>What a request looked like when it left the client (the client disposes the message itself).</summary>
internal sealed record RecordedRequest(string Method, string Path, string? Authorization);

/// <summary>Canned API answers in the shapes the API writes.</summary>
internal static class Responses
{
    public static HttpResponseMessage Problem(HttpStatusCode status, string json, int? retryAfterSeconds = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/problem+json"),
        };

        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(seconds));
        }

        return response;
    }

    public static HttpResponseMessage Json<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    public static HttpResponseMessage Status(HttpStatusCode status) => new(status);
}

/// <summary>A clock that moves only when told to.</summary>
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private DateTimeOffset _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

/// <summary>Per-circuit token storage, as UI.Web's CircuitTokenStore keeps it.</summary>
internal sealed class InMemoryTokenStore : ITokenStore
{
    private string? _accessToken;
    private string? _refreshToken;

    public ValueTask<string?> GetAccessTokenAsync() => ValueTask.FromResult(_accessToken);

    public ValueTask<string?> GetRefreshTokenAsync() => ValueTask.FromResult(_refreshToken);

    public ValueTask SetTokensAsync(string accessToken, string refreshToken)
    {
        _accessToken = accessToken;
        _refreshToken = refreshToken;
        return ValueTask.CompletedTask;
    }

    public ValueTask ClearAsync()
    {
        _accessToken = null;
        _refreshToken = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Unsigned JWTs: the UI reads <c>exp</c> and the claims, and never validates a signature (the API does).</summary>
internal static class TestJwt
{
    public static string ExpiringAt(DateTimeOffset expiresAt, string subject = "user-1")
    {
        string header = Base64Url("""{"alg":"HS256","typ":"JWT"}""");
        string payload = Base64Url(string.Create(
            CultureInfo.InvariantCulture,
            $$"""{"sub":"{{subject}}","email":"{{subject}}@example.com","exp":{{expiresAt.ToUnixTimeSeconds()}}}"""));
        return $"{header}.{payload}.not-a-signature";
    }

    private static string Base64Url(string json)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
#endif
