using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Application.Authorization;
using Trellis.Persistence.Identity;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// The identity seed the MigrationService runs on every deploy (<see cref="IdentitySeed"/>), against real
/// Identity and PostgreSQL: a rerun converges instead of duplicating, an account Identity rejects fails
/// the seed with every error named (the worker turns that into exit code 1), and a half-seeded admin is
/// repaired by the next run.
/// </summary>
public sealed class MigrationSeedTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    private const string AdminEmail = "seed-admin@integration.test";
    private const string AdminPassword = "Seed-admin-1!";
#if (UseMultitenancy)
    private const string TenantId = "seed-tenant";
#endif

    [Fact]
    public async Task Seeding_twice_converges_on_one_administrator_and_the_full_permission_catalogue()
    {
        await SeedAsync(AdminPassword);
        await SeedAsync(AdminPassword);

        using IServiceScope scope = CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>();

        (await userManager.Users.CountAsync(static user => user.Email == AdminEmail, TestContext.Current.CancellationToken)).ShouldBe(1);
        ApplicationUser admin = (await userManager.FindByEmailAsync(AdminEmail)).ShouldNotBeNull();
        (await userManager.GetRolesAsync(admin)).ShouldBe([Roles.Administrator]);
#if (UseMultitenancy)
        admin.TenantId.ShouldBe(TenantId);
#endif

        foreach ((string roleName, IReadOnlyList<string> permissions) in Roles.Permissions)
        {
            IdentityRole<Guid> role = (await roleManager.FindByNameAsync(roleName)).ShouldNotBeNull();
            IList<Claim> claims = await roleManager.GetClaimsAsync(role);
            claims.Where(static claim => claim.Type == AuthClaims.Permission).Select(static claim => claim.Value)
                .ShouldBe(permissions, ignoreOrder: true);
        }

        (await roleManager.Roles.CountAsync(TestContext.Current.CancellationToken)).ShouldBe(Roles.Permissions.Count);
    }

    [Fact]
    public async Task A_password_identity_rejects_fails_the_seed_naming_every_error_and_creates_no_admin()
    {
        // Long enough for Seed:AdminPassword's [MinLength(8)], so it reaches Identity — which wants a
        // digit, an upper-case letter and a symbol as well.
        InvalidOperationException failure = await Should.ThrowAsync<InvalidOperationException>(() => SeedAsync("password"));

        failure.Message.ShouldStartWith($"Creating the administrator '{AdminEmail}' failed:");
        failure.Message.ShouldContain("[PasswordRequiresDigit]");
        failure.Message.ShouldContain("[PasswordRequiresUpper]");
        failure.Message.ShouldContain("[PasswordRequiresNonAlphanumeric]");

        using IServiceScope scope = CreateScope();
        (await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(AdminEmail)).ShouldBeNull();
    }

    [Fact]
    public async Task A_rerun_repairs_an_administrator_left_without_its_role()
    {
        await SeedAsync(AdminPassword);

        // What an interrupted first run used to leave behind: the account, but not its role.
        using (IServiceScope scope = CreateScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            ApplicationUser admin = (await userManager.FindByEmailAsync(AdminEmail)).ShouldNotBeNull();
            IdentitySeed.EnsureSucceeded(await userManager.RemoveFromRoleAsync(admin, Roles.Administrator), "test setup");
        }

        await SeedAsync(AdminPassword);

        using IServiceScope readScope = CreateScope();
        var users = readScope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser repaired = (await users.FindByEmailAsync(AdminEmail)).ShouldNotBeNull();
        (await users.IsInRoleAsync(repaired, Roles.Administrator)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_rerun_never_changes_an_existing_administrators_password()
    {
        await SeedAsync(AdminPassword);
        await SeedAsync("Another-password-2!");

        using IServiceScope scope = CreateScope();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser admin = (await userManager.FindByEmailAsync(AdminEmail)).ShouldNotBeNull();
        (await userManager.CheckPasswordAsync(admin, AdminPassword)).ShouldBeTrue();
    }

    [Fact]
    public void EnsureSucceeded_passes_a_success_through()
        => Should.NotThrow(() => IdentitySeed.EnsureSucceeded(IdentityResult.Success, "Anything"));

    [Fact]
    public void EnsureSucceeded_names_the_step_and_joins_every_error_with_its_code()
    {
        IdentityResult failed = IdentityResult.Failed(
            new IdentityError { Code = "DuplicateUserName", Description = "Username 'a@b.c' is already taken." },
            new IdentityError { Code = "InvalidEmail", Description = "Email 'a@b' is invalid." });

        InvalidOperationException failure = Should.Throw<InvalidOperationException>(
            () => IdentitySeed.EnsureSucceeded(failed, "Creating the administrator 'a@b.c'"));

        failure.Message.ShouldBe(
            "Creating the administrator 'a@b.c' failed: [DuplicateUserName] Username 'a@b.c' is already taken. "
            + "[InvalidEmail] Email 'a@b' is invalid.");
    }

    /// <summary>One worker run's identity seed, in its own scope as the worker does it.</summary>
    private async Task SeedAsync(string password)
    {
        using IServiceScope scope = CreateScope();
        await IdentitySeed.EnsureRolesAndPermissionsAsync(scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole<Guid>>>());
        await IdentitySeed.EnsureAdministratorAsync(
            scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>(),
            AdminEmail,
            password
#if (UseMultitenancy)
            , TenantId
#endif
            );
    }
}
