extern alias apihost;

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using XTemplate.Api.Contracts.Auth;
using XTemplate.Application.Authorization;
using XTemplate.Persistence;
using XTemplate.Persistence.Identity;
using Xunit;

namespace XTemplate.ApiTests.InProcess;

/// <summary>
/// In-process test host: the REAL HTTP pipeline (auth, permissions, idempotency, rate limiting,
/// problem details) over a real PostgreSQL container — and unlike the Aspire E2E, it runs inside
/// the SDK container, so `./scripts/dotnet.sh test` covers the Presentation ring.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<apihost::Program>, IAsyncLifetime
{
    public const string AdminEmail = "admin@inprocess.test";
    public const string AdminPassword = "InProc3ss-admin!";

    // One container per fixture, no reuse: reuse is experimental and the Ryuk reaper kills
    // "reusable" containers when a parallel test assembly's session ends mid-run.
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:18-alpine")
        .WithDatabase("xtemplate_apitests")
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
        builder.UseSetting("ConnectionStrings:xtemplate-db", _postgres.GetConnectionString());
        builder.UseSetting("Authentication:SigningKey", "in-process-test-signing-key-32-chars-minimum!!");
    }

    /// <summary>A client authenticated as the seeded admin (all permissions).</summary>
    public async Task<HttpClient> CreateAdminClientAsync(CancellationToken cancellationToken)
        => await CreateAuthenticatedClientAsync(AdminEmail, AdminPassword, cancellationToken);

    /// <summary>Registers a fresh member (read-only permissions) and returns a client for them.</summary>
    public async Task<HttpClient> CreateMemberClientAsync(CancellationToken cancellationToken)
    {
        string email = $"member-{Guid.NewGuid():N}@inprocess.test";
        HttpClient anonymous = CreateClient();
        (await anonymous.PostAsJsonAsync("/api/v1/auth/register", new RegisterRequest(email, AdminPassword), cancellationToken))
            .EnsureSuccessStatusCode();
        return await CreateAuthenticatedClientAsync(email, AdminPassword, cancellationToken);
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
            await roleManager.CreateAsync(role);
            foreach (string permission in permissions)
            {
                await roleManager.AddClaimAsync(role, new Claim(AuthClaims.Permission, permission));
            }
        }

        var userManager = services.GetRequiredService<UserManager<ApplicationUser>>();
        if (await userManager.FindByEmailAsync(AdminEmail) is null)
        {
            var admin = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = AdminEmail, Email = AdminEmail, EmailConfirmed = true };
            await userManager.CreateAsync(admin, AdminPassword);
            await userManager.AddToRoleAsync(admin, Roles.Administrator);
        }
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }
}
