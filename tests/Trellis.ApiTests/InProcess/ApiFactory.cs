extern alias apihost;

#if (UseRedis)
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection.Extensions;
#endif
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Testcontainers.PostgreSql;
using Trellis.Api.Contracts.Auth;
using Trellis.Application.Abstractions;
using Trellis.Application.Authorization;
using Trellis.Persistence;
using Trellis.Persistence.Identity;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// In-process test host: the REAL HTTP pipeline (auth, permissions, idempotency, rate limiting,
/// problem details) over a real PostgreSQL container — and unlike the Aspire E2E, it runs inside
/// the SDK container, so `./scripts/dotnet.sh test` covers the Presentation ring.
/// <para>
/// Every client this factory creates calls from its own simulated address (see
/// <see cref="ClientAddressHeader"/>), so each client is its own anonymous rate-limit partition,
/// as distinct browsers would be. Without it TestServer reports no remote address at all, every
/// anonymous request lands in one shared partition, and a fixture's logins and registrations
/// drain the same 10/min auth bucket.
/// </para>
/// </summary>
public class ApiFactory : WebApplicationFactory<apihost::Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@inprocess.test";
    public const string AdminPassword = "InProc3ss-admin!";
    // 64 bytes: long enough that a test can mint HS384/HS512 tokens with the same key, so a
    // foreign-algorithm token differs from a valid one in nothing but its algorithm.
    public const string SigningKey = "in-process-test-signing-key-long-enough-to-mint-hs384-and-hs512!";
#if (UseMultitenancy)
    /// <summary>The seeded admin's tenant; <see cref="TenantB"/> gets its own administrator on first use.</summary>
    public const string TenantA = "tenant-a";
    public const string TenantB = "tenant-b";
#endif

    /// <summary>
    /// Test-only request header naming the address the request appears to come from. The startup
    /// filter registered in <see cref="ConfigureWebHost"/> moves it into
    /// <c>HttpContext.Connection.RemoteIpAddress</c> and strips it before any application middleware runs.
    /// </summary>
    public const string ClientAddressHeader = "X-Test-Client-Address";

    // Shared by every factory in the test run, so no two clients anywhere reuse an address.
    private static int LastClientNumber;

    /// <summary>
    /// The host's <see cref="TimeProvider"/>: system time plus an offset a test may move forward instead
    /// of sleeping (an invoice that has to become overdue), and must <see cref="SettableClock.Reset"/>
    /// afterwards — the fixture is shared by its test class. A derived factory may register its own over it.
    /// </summary>
    public SettableClock Time { get; } = new();

    // One container per fixture, no reuse: reuse is experimental and the Ryuk reaper kills
    // "reusable" containers when a parallel test assembly's session ends mid-run.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("trellis_apitests")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();

        using IServiceScope scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        await SeedIdentityAsync(scope.ServiceProvider);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:trellis-db", _postgres.GetConnectionString());
        builder.UseSetting("Authentication:SigningKey", SigningKey);
        // Fixtures register throwaway members; do not depend on appsettings.Development.json opting in.
        builder.UseSetting("Registration:Mode", "Open");

        // Inserted FIRST: startup filters wrap in registration order, so this one is outermost and the
        // simulated address is in place before any middleware reads it, including the forwarded-headers
        // pass the WebHost inserts when ASPNETCORE_FORWARDEDHEADERS_ENABLED is set.
        builder.ConfigureServices(static services =>
            services.Insert(0, ServiceDescriptor.Transient<IStartupFilter, ClientAddressStartupFilter>()));
        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
#if (UseRedis)
        // The UseRedis toggle wires a Redis L2 into HybridCache via Aspire; there is no Redis in
        // this host, so drop the distributed cache and run L1-only — otherwise every cache touch 500s.
        // Aspire registers a Redis health check alongside it; without this setting /health answers 503.
        builder.UseSetting("Aspire:StackExchange:Redis:DisableHealthChecks", "true");
        builder.ConfigureServices(static services => services.RemoveAll<IDistributedCache>());
#endif
    }

    /// <summary>Every client gets its own address, hence its own anonymous rate-limit partition.</summary>
    protected override void ConfigureClient(HttpClient client)
    {
        base.ConfigureClient(client);
        client.DefaultRequestHeaders.Add(ClientAddressHeader, NextClientAddress().ToString());
    }

    /// <summary>A client whose requests come from <paramref name="address"/> (several clients may share one, like users behind one host).</summary>
    public HttpClient CreateClientFrom(IPAddress address)
    {
        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Remove(ClientAddressHeader);
        client.DefaultRequestHeaders.Add(ClientAddressHeader, address.ToString());
        return client;
    }

    /// <summary>A client authenticated as the seeded admin (all permissions).</summary>
    public async Task<HttpClient> CreateAdminClientAsync(CancellationToken cancellationToken)
        => await CreateAuthenticatedClientAsync(AdminEmail, AdminPassword, cancellationToken);

#if (UseMultitenancy)
    /// <summary>An Administrator in the given tenant (created on first use), logged in. <see cref="TenantA"/> is the seeded admin.</summary>
    public async Task<HttpClient> CreateAdminClientAsync(string tenantId, CancellationToken cancellationToken)
    {
        string email = string.Equals(tenantId, TenantA, StringComparison.Ordinal) ? AdminEmail : $"admin-{tenantId}@inprocess.test";
        using (IServiceScope scope = Services.CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            if (await userManager.FindByEmailAsync(email) is null)
            {
                var admin = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = email, Email = email, EmailConfirmed = true, TenantId = tenantId };
                await userManager.CreateAsync(admin, AdminPassword);
                await userManager.AddToRoleAsync(admin, Roles.Administrator);
            }
        }

        return await CreateAuthenticatedClientAsync(email, AdminPassword, cancellationToken);
    }
#endif

    /// <summary>Registers a fresh member (read-only permissions) and returns a client for them.</summary>
    public async Task<HttpClient> CreateMemberClientAsync(CancellationToken cancellationToken)
    {
        string email = $"member-{Guid.NewGuid():N}@inprocess.test";
        HttpClient anonymous = CreateClient();
        (await anonymous.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, AdminPassword), cancellationToken))
            .EnsureSuccessStatusCode();
        return await CreateAuthenticatedClientAsync(email, AdminPassword, cancellationToken);
    }

    /// <summary>
    /// A client holding exactly <paramref name="permissions"/> (none at all when empty), for pinning what
    /// each endpoint requires. The token is minted by the host's own <see cref="ITokenProvider"/>, so it is
    /// the token a real sign-in would carry, signed, scoped and shaped the same, for a user who exists only
    /// in the token: no role, no Identity row, no login request spent against the rate limit.
    /// </summary>
    public HttpClient CreateClientWithPermissions(params string[] permissions)
    {
        foreach (string permission in permissions)
        {
            Permissions.All.Contains(permission, StringComparer.Ordinal).ShouldBeTrue($"'{permission}' is not in the permission catalogue");
        }

        Guid userId = Guid.CreateVersion7();
        var user = new AuthenticatedUser(
            userId,
            $"scoped-{userId:N}@inprocess.test",
            permissions
#if (UseMultitenancy)
            , TenantA
#endif
            );

        string token;
        using (IServiceScope scope = Services.CreateScope())
        {
            token = scope.ServiceProvider.GetRequiredService<ITokenProvider>().CreateAccessToken(user);
        }

        HttpClient client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>
    /// A second host over the SAME database with <paramref name="configureServices"/> applied after the
    /// app's own registrations — for a fault the real pipeline cannot produce on demand (a commit that
    /// conflicts or throws, a cache that cancels). Disposed with this factory.
    /// </summary>
    public WebApplicationFactory<apihost::Program> WithTestServices(Action<IServiceCollection> configureServices)
        => WithWebHostBuilder(builder => builder.ConfigureTestServices(configureServices));

    /// <summary>
    /// A client of <paramref name="host"/> signed in as the seeded admin. The token pair is minted by
    /// THIS host (same signing key, same database), so a derived host whose own login path is faulted
    /// still gets an authenticated client.
    /// </summary>
    public async Task<HttpClient> CreateAdminClientOnAsync(WebApplicationFactory<apihost::Program> host, CancellationToken cancellationToken)
    {
        TokenResponse tokens = await LoginAsAdminAsync(cancellationToken);
        HttpClient client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    /// <summary>Signs in as the seeded admin from a fresh client and returns the token pair.</summary>
    public async Task<TokenResponse> LoginAsAdminAsync(CancellationToken cancellationToken)
    {
        HttpResponseMessage response = await CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login", new LoginRequest(AdminEmail, AdminPassword), cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken))!;
    }

    private async Task<HttpClient> CreateAuthenticatedClientAsync(string email, string password, CancellationToken cancellationToken)
    {
        HttpClient client = CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/v1/auth/login", new LoginRequest(email, password), cancellationToken);
        response.EnsureSuccessStatusCode();
        TokenResponse tokens = (await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    // 10.0.0.0/8: never loopback (so never a trusted proxy by default) and roomy enough for any run.
    private static IPAddress NextClientAddress()
    {
        int number = Interlocked.Increment(ref LastClientNumber);
        return new IPAddress([10, (byte)(number >> 16), (byte)(number >> 8), (byte)number]);
    }

    private static async Task SeedIdentityAsync(IServiceProvider services)
    {
        var roleManager = services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        foreach ((string roleName, IReadOnlyList<string> permissions) in Roles.Permissions)
        {
            if (await roleManager.FindByNameAsync(roleName) is not null)
            {
                continue;   // idempotent: safe against any prior seeding of the same database
            }

            var role = new IdentityRole<Guid>(roleName) { Id = Guid.CreateVersion7() };
            IdentitySeed.EnsureSucceeded(await roleManager.CreateAsync(role), $"Creating role '{roleName}'");
            foreach (string permission in permissions)
            {
                IdentitySeed.EnsureSucceeded(
                    await roleManager.AddClaimAsync(role, new Claim(AuthClaims.Permission, permission)),
                    $"Granting '{permission}' to role '{roleName}'");
            }
        }

        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        if (await userManager.FindByEmailAsync(AdminEmail) is null)
        {
            var admin = new ApplicationUser
            {
                Id = Guid.CreateVersion7(),
                UserName = AdminEmail,
                Email = AdminEmail,
                EmailConfirmed = true,
#if (UseMultitenancy)
                TenantId = TenantA,
#endif
            };
            IdentitySeed.EnsureSucceeded(await userManager.CreateAsync(admin, AdminPassword), $"Creating the administrator '{AdminEmail}'");
            IdentitySeed.EnsureSucceeded(await userManager.AddToRoleAsync(admin, Roles.Administrator), $"Adding '{AdminEmail}' to role '{Roles.Administrator}'");
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>System time plus an offset a test can move forward and reset.</summary>
    public sealed class SettableClock : TimeProvider
    {
        private long _offsetTicks;

        public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

        public void Reset() => Interlocked.Exchange(ref _offsetTicks, 0);

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));
    }

    /// <summary>Applies <see cref="ClientAddressHeader"/> as the connection's remote address, then removes the header.</summary>
    private sealed class ClientAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(static (context, nextMiddleware) =>
            {
                if (context.Request.Headers.Remove(ClientAddressHeader, out var value)
                    && IPAddress.TryParse(value.ToString(), out IPAddress? address))
                {
                    context.Connection.RemoteIpAddress = address;
                }

                return nextMiddleware(context);
            });

            next(app);
        };
    }
}
