using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Trellis.Api.Contracts.Auth;
using Trellis.Persistence.Identity;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// The session lifecycle over real HTTP: registration policy, rotation, replay, logout, revoke-all,
/// lockout and refresh-token expiry, against real Identity and PostgreSQL.
/// <para>
/// Every test registers its own user and uses its own clients, and each client is its own rate-limit
/// partition (<see cref="ApiFactory"/>), so no test spends another's <c>auth</c> budget. The fixture
/// swaps in three observers: an adjustable clock (expiry), a counting password hasher (the
/// timing-oracle fix) and a capturing logger (security events).
/// </para>
/// </summary>
public sealed class AuthFlowTests(AuthFlowTests.Factory factory) : IClassFixture<AuthFlowTests.Factory>
{
    private const string Password = "Fl0w-test-pass!";
    private const string WrongGuess = "Wr0ng-guess-1234";

    [Fact]
    public async Task Register_WhenRegistrationIsOpen_CreatesAnAccountThatCanSignIn()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = factory.CreateClient();
        string email = NewEmail();

        HttpResponseMessage registered = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, Password), ct);

        registered.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await registered.Content.ReadFromJsonAsync<RegisterResponse>(ct))!.UserId.ShouldNotBe(Guid.Empty);
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, Password), ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Register_WhenRegistrationIsClosed_Answers403ProblemAndCreatesNothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient closed = factory
            .WithWebHostBuilder(static builder => builder.UseSetting("Registration:Mode", "Closed"))
            .CreateClient();
        string email = NewEmail();

        HttpResponseMessage response = await closed.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, Password), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(response, ct)).ShouldBe("Auth.RegistrationClosed");

        // Nothing was created: the address is not an account on either host.
        (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, Password), ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_ReplayOfAConsumedToken_Is401AndRevokesTheWholeFamily()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = factory.CreateClient();
        string email = await RegisterAsync(client, ct);

        TokenResponse first = await LoginAsync(client, email, ct);
        TokenResponse second = await RefreshAsync(client, first.RefreshToken, ct);
        second.RefreshToken.ShouldNotBe(first.RefreshToken, StringComparer.Ordinal);

        // Presenting the consumed token again is the stolen-token signal...
        HttpResponseMessage replay = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(first.RefreshToken), ct);
        replay.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ProblemCodeAsync(replay, ct)).ShouldBe("Auth.InvalidRefreshToken");

        // ...so the token minted from it dies too, even though nobody used it yet.
        HttpResponseMessage descendant = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(second.RefreshToken), ct);
        descendant.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ProblemCodeAsync(descendant, ct)).ShouldBe("Auth.InvalidRefreshToken");
    }

    [Fact]
    public async Task Logout_IsIdempotentAndTheTokenStopsWorking()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = factory.CreateClient();
        string email = await RegisterAsync(client, ct);
        TokenResponse session = await LoginAsync(client, email, ct);

        (await client.PostAsJsonAsync("/api/v1/auth/logout", new LogoutRequest(session.RefreshToken), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.PostAsJsonAsync("/api/v1/auth/logout", new LogoutRequest(session.RefreshToken), ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(session.RefreshToken), ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task RevokeAll_InvalidatesTheCallersOtherSessions()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient laptop = factory.CreateClient();
        HttpClient phone = factory.CreateClient();
        string email = await RegisterAsync(laptop, ct);

        TokenResponse laptopSession = await LoginAsync(laptop, email, ct);
        TokenResponse phoneSession = await LoginAsync(phone, email, ct);

        using var revokeAll = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/revoke-all");
        revokeAll.Headers.Authorization = new AuthenticationHeaderValue("Bearer", laptopSession.AccessToken);
        (await laptop.SendAsync(revokeAll, ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await phone.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(phoneSession.RefreshToken), ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await laptop.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(laptopSession.RefreshToken), ct))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_FiveWrongPasswords_LockTheAccountAndTheRightPasswordStays403()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = factory.CreateClient();
        string email = NewEmail();
        HttpResponseMessage registered = await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, Password), ct);
        Guid userId = (await registered.Content.ReadFromJsonAsync<RegisterResponse>(ct))!.UserId;

        // MaxFailedAccessAttempts is 5: four plain failures...
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            HttpResponseMessage failed = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, WrongGuess), ct);
            failed.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, $"attempt {attempt}");
            (await ProblemCodeAsync(failed, ct)).ShouldBe("Auth.InvalidCredentials");
        }

        // ...the fifth trips the lockout...
        HttpResponseMessage locking = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, WrongGuess), ct);
        locking.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(locking, ct)).ShouldBe("Auth.AccountLockedOut");

        // ...and while locked even the right password is refused, so a 403 never confirms a password.
        HttpResponseMessage locked = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, Password), ct);
        locked.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ProblemCodeAsync(locked, ct)).ShouldBe("Auth.AccountLockedOut");

        // The security events name the account by id, at the documented levels, and never carry a password.
        IReadOnlyList<CapturedLog> events = factory.Logs.ForUser(userId);
        events.Count(static e => e.EventName == "LoginFailed" && e.Level == LogLevel.Information).ShouldBe(4);
        events.Count(static e => e.EventName == "AccountLockedOut" && e.Level == LogLevel.Warning).ShouldBe(1);
        events.Count(static e => e.EventName == "LoginRejectedLockedOut").ShouldBe(1);
        factory.Logs.All.ShouldNotContain(static e => e.Message.Contains(WrongGuess) || e.Message.Contains(Password));
    }

    [Fact]
    public async Task Login_UnknownEmail_SpendsThePasswordHashAWrongPasswordSpends()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = factory.CreateClient();
        string email = await RegisterAsync(client, ct);
        string unknown = $"ghost-{Guid.NewGuid():N}@inprocess.test";

        int before = factory.Hasher.Verifications;
        HttpResponseMessage noSuchAccount = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(unknown, Password), ct);
        int forUnknown = factory.Hasher.Verifications - before;

        before = factory.Hasher.Verifications;
        HttpResponseMessage wrongPassword = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, WrongGuess), ct);
        int forWrongPassword = factory.Hasher.Verifications - before;

        // Indistinguishable answers, and the same hash work behind each (no timing oracle).
        noSuchAccount.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        wrongPassword.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await ProblemCodeAsync(noSuchAccount, ct)).ShouldBe(await ProblemCodeAsync(wrongPassword, ct));
        forUnknown.ShouldBe(1);
        forWrongPassword.ShouldBe(1);

        // The miss is logged with the address masked, never as typed.
        factory.Logs.All.ShouldContain(static e => e.EventName == "LoginFailedUnknownAccount" && e.Message.Contains("g***@inprocess.test"));
        factory.Logs.All.ShouldNotContain(e => e.Message.Contains(unknown));
    }

    [Fact]
    public async Task Refresh_ExpiredToken_IsRejected()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = factory.CreateClient();
        string email = await RegisterAsync(client, ct);
        TokenResponse early = await LoginAsync(client, email, ct);
        TokenResponse late = await LoginAsync(client, email, ct);

        try
        {
            // RefreshTokenDays is 7. A minute before expiry the token still rotates...
            factory.Clock.Advance(TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1));
            (await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(early.RefreshToken), ct))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            // ...a minute after, its twin is dead.
            factory.Clock.Advance(TimeSpan.FromMinutes(2));
            HttpResponseMessage expired = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(late.RefreshToken), ct);
            expired.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await ProblemCodeAsync(expired, ct)).ShouldBe("Auth.InvalidRefreshToken");
        }
        finally
        {
            factory.Clock.Reset();
        }
    }

    [Theory]
    [InlineData("/api/v1/auth/refresh")]
    [InlineData("/api/v1/auth/logout")]
    [InlineData("/api/v1/auth/login")]
    public async Task AuthEndpoints_BodyWithoutItsFields_Answers400(string route)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // A missing field binds as null; before the validators existed it reached the token hash
        // or Identity and came back as a 500.
        using var empty = new StringContent("{}", Encoding.UTF8, "application/json");
        HttpResponseMessage response = await factory.CreateClient().PostAsync(route, empty, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
    }

    [Fact]
    public async Task Login_OversizedPassword_Is400NotAHashOrALockoutStrike()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient client = factory.CreateClient();
        string email = await RegisterAsync(client, ct);
        int before = factory.Hasher.Verifications;

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new LoginRequest(email, new string('p', 129)), ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        factory.Hasher.Verifications.ShouldBe(before);
    }

    private static string NewEmail() => $"flow-{Guid.NewGuid():N}@inprocess.test";

    private static async Task<string> RegisterAsync(HttpClient client, CancellationToken ct)
    {
        string email = NewEmail();
        (await client.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, Password), ct))
            .EnsureSuccessStatusCode();
        return email;
    }

    private static async Task<TokenResponse> LoginAsync(HttpClient client, string email, CancellationToken ct)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest(email, Password), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(ct))!;
    }

    private static async Task<TokenResponse> RefreshAsync(HttpClient client, string refreshToken, CancellationToken ct)
    {
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/v1/auth/refresh", new RefreshRequest(refreshToken), ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(ct))!;
    }

    /// <summary>The problem's error code: the <c>code</c> member when present, else the title that carries it today.</summary>
    private static async Task<string?> ProblemCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/problem+json");
        using JsonDocument problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return problem.RootElement.TryGetProperty("code", out JsonElement code)
            ? code.GetString()
            : problem.RootElement.GetProperty("title").GetString();
    }

    /// <summary><see cref="ApiFactory"/> with registration explicitly open and the three observers swapped in.</summary>
    public sealed class Factory : ApiFactory
    {
        public AdjustableClock Clock { get; } = new();

        public CountingPasswordHasher Hasher { get; } = new();

        public CapturingLoggerProvider Logs { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);

            // Stated here rather than inherited from appsettings.Development.json: these tests are
            // about the registration policy, so they do not lean on the environment's default.
            builder.UseSetting("Registration:Mode", "Open");
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(Clock);
                services.AddSingleton<IPasswordHasher<ApplicationUser>>(Hasher);
                services.AddSingleton<ILoggerProvider>(Logs);
            });
        }
    }

    /// <summary>System time plus an offset a test can move forward and reset.</summary>
    public sealed class AdjustableClock : TimeProvider
    {
        private long _offsetTicks;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

        public void Reset() => Interlocked.Exchange(ref _offsetTicks, 0);

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));
    }

    /// <summary>The real hasher, counting verifications.</summary>
    public sealed class CountingPasswordHasher : IPasswordHasher<ApplicationUser>
    {
        private readonly PasswordHasher<ApplicationUser> _inner = new();
        private int _verifications;

        public int Verifications => Volatile.Read(ref _verifications);

        public string HashPassword(ApplicationUser user, string password) => _inner.HashPassword(user, password);

        public PasswordVerificationResult VerifyHashedPassword(ApplicationUser user, string hashedPassword, string providedPassword)
        {
            Interlocked.Increment(ref _verifications);
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }

    public sealed record CapturedLog(string Category, LogLevel Level, string? EventName, string Message);

    /// <summary>Keeps every log line the host writes, so tests can assert on security events.</summary>
    public sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedLog> _entries = new();

        public IReadOnlyList<CapturedLog> All => [.. _entries];

        public IReadOnlyList<CapturedLog> ForUser(Guid userId)
            => [.. _entries.Where(entry => entry.Message.Contains(userId.ToString(), StringComparison.OrdinalIgnoreCase))];

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public void Dispose()
        {
            // Nothing to release; the entries outlive the host on purpose.
        }

        private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new CapturedLog(category, logLevel, eventId.Name, formatter(state, exception)));
        }
    }
}
