using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Shouldly;
using Trellis.Api.Common.Authentication;
using Trellis.Api.Common.Hardening;
using Trellis.Api.Common.Idempotency;
using Trellis.Api.Contracts.Auth;
using Trellis.Api.Contracts.Invoices;
using Trellis.Application.Authorization;
using Trellis.Application.Options;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// The secure-by-default contract of the API surface (ADR-0021), read from what every endpoint
/// declares in the routing table and checked on the wire. The group authenticates by default; these
/// tests are what turn "remember to add RequirePermission / AllowAnonymous / RequireIdempotency /
/// WithDescription" from a convention into a build failure.
/// </summary>
public sealed class EndpointMetadataTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string GroupPrefix = "/api/v{version:apiVersion}/";

    /// <summary>
    /// The only endpoints reachable without a token. Adding one is a security decision: it needs
    /// <c>.AllowAnonymous()</c> on the endpoint AND an entry here, reviewed together.
    /// </summary>
    private static readonly HashSet<string> AnonymousAllowlist = new(StringComparer.Ordinal)
    {
        "POST auth/register",
        "POST auth/login",
        "POST auth/refresh",
        "POST auth/logout",
    };

    /// <summary>Signed in is the whole requirement: acting on the caller's own sessions needs no permission.</summary>
    private static readonly HashSet<string> AuthenticatedOnly = new(StringComparer.Ordinal)
    {
        "POST auth/revoke-all",
    };

    /// <summary>
    /// The one permission each remaining endpoint requires. "Has some catalogued permission" would pass
    /// a write declared with Read (anyone who may look could pay or void); this table is what turns that
    /// mix-up into a red build. A new endpoint is a new row, reviewed with its <c>RequirePermission</c>.
    /// </summary>
    private static readonly Dictionary<string, string> ExpectedPermissions = new(StringComparer.Ordinal)
    {
        ["GET invoices/{id}"] = Permissions.Invoices.Read,
        ["GET invoices/overdue"] = Permissions.Invoices.Read,
        ["POST invoices"] = Permissions.Invoices.Write,
        ["POST invoices/{id}/lines"] = Permissions.Invoices.Write,
        ["POST invoices/{id}/issue"] = Permissions.Invoices.Write,
        ["POST invoices/{id}/pay"] = Permissions.Invoices.Write,
        ["POST invoices/{id}/void"] = Permissions.Invoices.Write,
    };

    /// <summary>Endpoints that accept a guessable credential carry the strict auth limiter; token endpoints must not.</summary>
    private static readonly HashSet<string> CredentialEndpoints = new(StringComparer.Ordinal)
    {
        "POST auth/register",
        "POST auth/login",
    };

    [Fact]
    public void The_versioned_group_requires_authorization_on_every_endpoint()
    {
        // Group-level RequireAuthorization() stamps every endpoint, the anonymous ones included
        // (AllowAnonymous wins at runtime). An endpoint without it was mapped outside the group policy.
        string[] unguarded = [.. ApiEndpoints().Where(static e => !e.Metadata.OfType<IAuthorizeData>().Any()).Select(static e => e.Key)];

        unguarded.ShouldBeEmpty();
    }

    [Fact]
    public void Every_endpoint_is_either_anonymous_or_authorized_never_both_never_neither()
    {
        List<string> violations = [];
        foreach (ApiEndpoint endpoint in ApiEndpoints())
        {
            bool anonymous = endpoint.Metadata.OfType<IAllowAnonymous>().Any();
            bool requiresPolicy = endpoint.Metadata.OfType<IAuthorizeData>().Any(static data => data.Policy is not null);

            if (anonymous && requiresPolicy)
            {
                violations.Add($"{endpoint.Key}: AllowAnonymous and a permission contradict each other");
            }
            else if (!anonymous && !endpoint.Metadata.OfType<IAuthorizeData>().Any())
            {
                violations.Add($"{endpoint.Key}: neither anonymous nor authorized");
            }
        }

        violations.ShouldBeEmpty();
    }

    [Fact]
    public void Anonymous_endpoints_are_exactly_the_allowlist()
    {
        string[] anonymous = [.. ApiEndpoints().Where(static e => e.Metadata.OfType<IAllowAnonymous>().Any()).Select(static e => e.Key)];

        anonymous.ShouldBe(AnonymousAllowlist, StringComparer.Ordinal, ignoreOrder: true,
            "an endpoint reachable without a token must call .AllowAnonymous() AND be listed in AnonymousAllowlist");
    }

    [Fact]
    public void Every_authenticated_endpoint_requires_a_catalogued_permission()
    {
        // Group-level authentication would otherwise mask a forgotten RequirePermission as "signed in is enough".
        string[] missing = [.. ApiEndpoints()
            .Where(static e => !e.Metadata.OfType<IAllowAnonymous>().Any() && !AuthenticatedOnly.Contains(e.Key))
            .Where(static e => !e.Metadata.OfType<IAuthorizeData>().Any(static data => data.Policy is { } policy && Permissions.All.Contains(policy, StringComparer.Ordinal)))
            .Select(static e => e.Key)];

        missing.ShouldBeEmpty("declare the permission with .RequirePermission(Permissions.X.Y)");
    }

    [Fact]
    public void Every_permissioned_endpoint_requires_exactly_its_expected_permission()
    {
        List<string> violations = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (ApiEndpoint endpoint in ApiEndpoints()
                     .Where(static e => !e.Metadata.OfType<IAllowAnonymous>().Any() && !AuthenticatedOnly.Contains(e.Key)))
        {
            seen.Add(endpoint.Key);
            string[] declared = [.. endpoint.Metadata.OfType<IAuthorizeData>()
                .Select(static data => data.Policy)
                .OfType<string>()
                .Where(static policy => Permissions.All.Contains(policy, StringComparer.Ordinal))
                .Distinct(StringComparer.Ordinal)];

            if (!ExpectedPermissions.TryGetValue(endpoint.Key, out string? expected))
            {
                violations.Add($"{endpoint.Key}: no row in ExpectedPermissions (declares {string.Join(", ", declared)})");
            }
            else if (declared.Length != 1 || !string.Equals(declared[0], expected, StringComparison.Ordinal))
            {
                violations.Add($"{endpoint.Key}: requires [{string.Join(", ", declared)}], expected exactly {expected}");
            }
        }

        violations.AddRange(ExpectedPermissions.Keys.Where(key => !seen.Contains(key)).Select(static key => $"{key}: a row for an endpoint that no longer exists"));
        violations.ShouldBeEmpty("each permissioned endpoint declares exactly one .RequirePermission(...), the one ExpectedPermissions names");
    }

    [Theory]
    [InlineData("GET", "invoices/{id}", HttpStatusCode.NotFound)]
    [InlineData("GET", "invoices/overdue", HttpStatusCode.OK)]
    [InlineData("POST", "invoices", HttpStatusCode.Created)]
    [InlineData("POST", "invoices/{id}/lines", HttpStatusCode.NotFound)]
    [InlineData("POST", "invoices/{id}/issue", HttpStatusCode.NotFound)]
    [InlineData("POST", "invoices/{id}/pay", HttpStatusCode.NotFound)]
    [InlineData("POST", "invoices/{id}/void", HttpStatusCode.NotFound)]
    public async Task Each_endpoint_admits_its_one_permission_and_refuses_every_other(string method, string route, HttpStatusCode admitted)
    {
        // Wire-level, beside the table above: the permission alone gets the endpoint's own answer (a 404
        // for an id that does not exist proves authorization passed), and every OTHER permission, all of
        // them together, is a 403 problem. Exact statuses, never "not 401/403".
        CancellationToken ct = TestContext.Current.CancellationToken;
        string required = ExpectedPermissions[$"{method} {route}"];
        string[] others = [.. Permissions.All.Where(permission => !string.Equals(permission, required, StringComparison.Ordinal))];

        HttpResponseMessage withIt = await SendAsync(factory.CreateClientWithPermissions(required), method, route, ct);
        HttpResponseMessage withEverythingElse = await SendAsync(factory.CreateClientWithPermissions(others), method, route, ct);

        withIt.StatusCode.ShouldBe(admitted, await withIt.Content.ReadAsStringAsync(ct));
        withEverythingElse.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await withEverythingElse.Content.ReadFromJsonAsync<Trellis.Api.Contracts.Common.ProblemResponse>(ct)).ShouldNotBeNull().Code.ShouldBe("Auth.Forbidden");
    }

    [Fact]
    public async Task A_token_without_permissions_is_authenticated_but_forbidden()
    {
        HttpResponseMessage response = await factory.CreateClientWithPermissions()
            .GetAsync("/api/v1/invoices/overdue", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    /// <summary>A request to <paramref name="route"/> with a fresh id and a body that would pass validation, so only authorization can stop it.</summary>
    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string method, string route, CancellationToken ct)
    {
        string path = "/api/v1/" + route.Replace("{id}", Guid.NewGuid().ToString(), StringComparison.Ordinal);
        if (string.Equals(method, HttpMethods.Get, StringComparison.Ordinal))
        {
            return await client.GetAsync(path, ct);
        }

        object? body = route switch
        {
            "invoices" => new CreateInvoiceRequest("Scoped Co", "s@s.co", "USD"),
            "invoices/{id}/lines" => new AddInvoiceLineRequest("Line", 10m),
            "invoices/{id}/issue" => new IssueInvoiceRequest(DateTimeOffset.UtcNow.AddDays(7)),
            _ => null,
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Add(Trellis.Api.Contracts.Common.ApiHeaders.IdempotencyKey, Guid.NewGuid().ToString());
        return await client.SendAsync(request, ct);
    }

    [Fact]
    public void Only_credential_endpoints_carry_the_strict_auth_rate_limit()
    {
        string[] withAuthPolicy = [.. ApiEndpoints()
            .Where(static e => e.Metadata.OfType<EnableRateLimitingAttribute>()
                .Any(static limit => string.Equals(limit.PolicyName, ApiHardeningExtensions.AuthRateLimitPolicy, StringComparison.Ordinal)))
            .Select(static e => e.Key)];

        withAuthPolicy.ShouldBe(CredentialEndpoints, StringComparer.Ordinal, ignoreOrder: true);
    }

    [Fact]
    public void Every_state_changing_endpoint_outside_auth_requires_idempotency()
    {
        string[] missing = [.. ApiEndpoints()
            .Where(static e => !string.Equals(e.Method, HttpMethods.Get, StringComparison.Ordinal)
                               && !e.Route.StartsWith("auth/", StringComparison.Ordinal))
            .Where(static e => !e.Metadata.OfType<IdempotencyMetadata>().Any())
            .Select(static e => e.Key)];

        missing.ShouldBeEmpty("write endpoints add .RequireIdempotency()");
    }

    [Fact]
    public void Every_endpoint_has_a_summary_and_a_description()
    {
        string[] undocumented = [.. ApiEndpoints()
            .Where(static e => e.Metadata.OfType<IEndpointSummaryMetadata>().All(static m => string.IsNullOrWhiteSpace(m.Summary))
                               || e.Metadata.OfType<IEndpointDescriptionMetadata>().All(static m => string.IsNullOrWhiteSpace(m.Description)))
            .Select(static e => e.Key)];

        undocumented.ShouldBeEmpty("every endpoint needs .WithSummary() and .WithDescription()");
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/alive")]
    public void Probes_live_outside_the_group_anonymous_and_exempt_from_rate_limiting(string path)
    {
        RouteEndpoint probe = RouteEndpoints()
            .Where(e => string.Equals(e.RoutePattern.RawText, path, StringComparison.Ordinal))
            .ShouldHaveSingleItem();

        probe.Metadata.OfType<IAuthorizeData>().ShouldBeEmpty("probes must stay reachable without credentials");
        probe.Metadata.OfType<DisableRateLimitingAttribute>().ShouldNotBeEmpty("a throttled probe reads as an unhealthy instance");
    }

    [Fact]
    public async Task Permission_policies_are_synthesized_for_catalogued_permissions_only()
    {
        IAuthorizationPolicyProvider provider = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        AuthorizationPolicy policy = (await provider.GetPolicyAsync(Permissions.Invoices.Read)).ShouldNotBeNull();
        policy.Requirements.OfType<PermissionRequirement>().ShouldHaveSingleItem().Permission.ShouldBe(Permissions.Invoices.Read);
        policy.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().ShouldHaveSingleItem();

        (await provider.GetPolicyAsync("invoices:raed")).ShouldBeNull("a misspelt name is an unknown policy, not a permission nobody holds");
        provider.AllowsCachingPolicies.ShouldBeTrue();
    }

    [Fact]
    public async Task Names_outside_the_catalogue_fall_back_to_registered_policies()
    {
        var services = new ServiceCollection();
        services.AddApiAuthentication();
        services.AddAuthorization(static options => options.AddPolicy("ops", static policy => policy.RequireRole("Ops")));
        await using ServiceProvider provider = services.BuildServiceProvider();

        AuthorizationPolicy named = (await provider.GetRequiredService<IAuthorizationPolicyProvider>().GetPolicyAsync("ops")).ShouldNotBeNull();

        named.Requirements.OfType<RolesAuthorizationRequirement>().ShouldHaveSingleItem().AllowedRoles.ShouldBe(["Ops"]);
    }

    [Fact]
    public void RequirePermission_rejects_an_unknown_permission_at_startup()
    {
        var endpoint = new RouteHandlerBuilder([]);

        Should.Throw<ArgumentException>(() => endpoint.RequirePermission("invoices:raed"))
            .Message.ShouldContain("invoices:raed");
        Should.NotThrow(() => endpoint.RequirePermission(Permissions.Invoices.Write));
    }

    [Theory]
    [InlineData("/alive")]
    [InlineData("/api/v1/invoices/overdue")]
    public async Task Every_response_carries_the_security_headers(string path)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // A probe (200) and a rejected API call (401 problem written by the JwtBearer challenge).
        HttpResponseMessage response = await factory.CreateClient().GetAsync(path, ct);

        response.Headers.GetValues("X-Content-Type-Options").ShouldHaveSingleItem().ShouldBe("nosniff");
        response.Headers.GetValues("Referrer-Policy").ShouldHaveSingleItem().ShouldBe("no-referrer");
        response.Headers.GetValues("X-Frame-Options").ShouldHaveSingleItem().ShouldBe("DENY");
        response.Headers.GetValues("Content-Security-Policy").ShouldHaveSingleItem().ShouldBe("frame-ancestors 'none'");
    }

    [Fact]
    public async Task Auth_responses_are_never_stored_by_a_cache()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        HttpResponseMessage tokens = await factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login", new LoginRequest(ApiFactory.AdminEmail, ApiFactory.AdminPassword), ct);
        HttpResponseMessage rejected = await factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/refresh", new RefreshRequest("not-a-refresh-token"), ct);

        tokens.StatusCode.ShouldBe(HttpStatusCode.OK);
        tokens.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        rejected.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        rejected.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task Api_responses_are_not_stored_by_a_cache_unless_the_endpoint_says_otherwise()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        HttpClient admin = await factory.CreateAdminClientAsync(ct);
        HttpResponseMessage created = await admin.PostWithIdempotencyKeyAsync(
            "/api/v1/invoices", new CreateInvoiceRequest("Cache Co", "c@c.co", "USD"), ct);

        // Per-user data, a 404 and a 201 alike: no-store unless the endpoint chose its own (the conditional
        // GET of one invoice does, for its 200 and 304: InvoiceContractTests).
        HttpResponseMessage read = await admin.GetAsync("/api/v1/invoices/overdue", ct);
        HttpResponseMessage missing = await admin.GetAsync($"/api/v1/invoices/{Guid.NewGuid()}", ct);
        read.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        missing.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();
        created.Headers.CacheControl.ShouldNotBeNull().NoStore.ShouldBeTrue();

        // An endpoint that sets its own keeps it (here a stand-in sets it before the endpoint runs).
        using var host = factory.WithTestServices(static services => services.AddTransient<IStartupFilter, OwnCacheControlStartupFilter>());
        HttpClient chosen = await factory.CreateAdminClientOnAsync(host, ct);
        HttpResponseMessage own = await chosen.GetAsync("/api/v1/invoices/overdue", ct);
        System.Net.Http.Headers.CacheControlHeaderValue kept = own.Headers.CacheControl.ShouldNotBeNull();
        kept.Private.ShouldBeTrue();
        kept.MaxAge.ShouldBe(TimeSpan.FromSeconds(60));
        kept.NoStore.ShouldBeFalse();
    }

    [Fact]
    public async Task Responses_outside_the_api_keep_their_own_caching()
    {
        // The health-check middleware writes its own "no-store, no-cache"; the API default (a bare no-store)
        // must not be what decides a probe's header.
        HttpResponseMessage alive = await factory.CreateClient().GetAsync("/alive", TestContext.Current.CancellationToken);

        alive.StatusCode.ShouldBe(HttpStatusCode.OK);
        alive.Headers.CacheControl.ShouldNotBeNull().NoCache.ShouldBeTrue();
    }

    [Fact]
    public async Task A_401_without_a_token_names_the_bearer_scheme()
    {
        HttpResponseMessage response = await factory.CreateClient()
            .GetAsync("/api/v1/invoices/overdue", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        AuthenticationHeaderValue challenge = response.Headers.WwwAuthenticate.ShouldHaveSingleItem();
        challenge.Scheme.ShouldBe("Bearer");
        challenge.Parameter.ShouldBeNull();
    }

    [Fact]
    public async Task A_401_for_a_rejected_token_says_invalid_token()
    {
        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "not.a.jwt");

        HttpResponseMessage response = await client.GetAsync("/api/v1/invoices/overdue", TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ShouldHaveSingleItem().ToString().ShouldBe("Bearer error=\"invalid_token\"");
    }

    [Fact]
    public async Task A_token_signed_with_another_algorithm_is_rejected()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Same key, issuer, audience and claims; only the algorithm differs. The HS256 control proves
        // the hand-minted token is otherwise valid, so the 401 is the algorithm pin and nothing else.
        HttpResponseMessage pinned = await GetOverdueWithTokenAsync(SecurityAlgorithms.HmacSha256, ct);
        HttpResponseMessage foreign = await GetOverdueWithTokenAsync(SecurityAlgorithms.HmacSha384, ct);

        pinned.StatusCode.ShouldBe(HttpStatusCode.OK);
        foreign.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private async Task<HttpResponseMessage> GetOverdueWithTokenAsync(string algorithm, CancellationToken ct)
    {
        AuthTokenOptions options = factory.Services.GetRequiredService<IOptions<AuthTokenOptions>>().Value;
        string subject = Guid.NewGuid().ToString();
        string token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(JwtRegisteredClaimNames.Sub, subject),
                new Claim(ClaimTypes.NameIdentifier, subject),
                new Claim(AuthClaims.Permission, Permissions.Invoices.Read),
#if (UseMultitenancy)
                // A valid token must carry the tenant (ADR-0022); this test is about the algorithm pin only.
                new Claim(Trellis.Application.Abstractions.ITenantContext.ClaimType, ApiFactory.TenantA),
#endif
            ]),
            Issuer = options.Issuer,
            Audience = options.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiFactory.SigningKey)), algorithm),
        });

        HttpClient client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.GetAsync("/api/v1/invoices/overdue", ct);
    }

    private IEnumerable<RouteEndpoint> RouteEndpoints()
        => factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();

    private List<ApiEndpoint> ApiEndpoints()
    {
        List<ApiEndpoint> endpoints = [.. RouteEndpoints()
            .Where(static e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) == true)
            .SelectMany(static e => (e.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? ["*"])
                .Select(method => new ApiEndpoint(method, Relative(e.RoutePattern.RawText!), e.Metadata)))];

        endpoints.ShouldNotBeEmpty("the versioned group maps no endpoints; did discovery break?");
        return endpoints;
    }

    private static string Relative(string rawText)
        => rawText.StartsWith(GroupPrefix, StringComparison.Ordinal) ? rawText[GroupPrefix.Length..] : rawText;

    private sealed record ApiEndpoint(string Method, string Route, EndpointMetadataCollection Metadata)
    {
        public string Key => $"{Method} {Route}";
    }
}

/// <summary>Stands in for an endpoint that chose its own caching: the header is on the response before the endpoint runs.</summary>
internal sealed class OwnCacheControlStartupFilter : IStartupFilter
{
    public const string Value = "private, max-age=60";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(static (context, nextMiddleware) =>
        {
            context.Response.Headers.CacheControl = Value;
            return nextMiddleware(context);
        });

        next(app);
    };
}

/// <summary>
/// Request-size limits are enforced by Kestrel, which TestServer does not emulate, so this fixture
/// runs the API on a real Kestrel listener (WebApplicationFactory.UseKestrel, .NET 10).
/// </summary>
public sealed class RequestLimitTests(RequestLimitTests.KestrelApiFactory factory) : IClassFixture<RequestLimitTests.KestrelApiFactory>
{
    [Fact]
    public async Task A_body_over_the_limit_is_rejected_with_413()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string oversizedEmail = new('a', (int)ApiHardeningExtensions.DefaultMaxRequestBodySize + 1);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest(oversizedEmail, "x")),
        };
        // Wait for the server's verdict before streaming the megabyte: Kestrel answers 413 without
        // reading the body, and the client then never races a connection reset.
        request.Headers.ExpectContinue = true;

        HttpResponseMessage response = await factory.CreateClient().SendAsync(request, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task A_body_under_the_limit_is_accepted()
    {
        HttpResponseMessage response = await factory.CreateClient().PostAsJsonAsync(
            "/api/v1/auth/login", new LoginRequest("nobody@nowhere.dev", "wrong-password"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>The API host on a real Kestrel listener (dynamic port).</summary>
    public sealed class KestrelApiFactory : ApiFactory
    {
        public KestrelApiFactory() => UseKestrel(0);
    }
}
