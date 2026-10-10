using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Trellis.Application.Authorization;

namespace Trellis.Persistence.Identity;

/// <summary>
/// Idempotent bootstrap of the identity store: the role/permission catalogue (<see cref="Roles.Permissions"/>)
/// and the first administrator. The MigrationService runs it on every deploy, so each step converges
/// on the desired state instead of assuming an empty database, and every Identity call is checked.
/// <para>
/// UserManager and RoleManager report failure as an <see cref="IdentityResult"/> value, not an exception;
/// discarding one is how a password Identity rejects used to end in "seeding completed" and no admin.
/// Here a failure throws (<see cref="EnsureSucceeded"/>): seeding is a one-shot deployment step whose
/// failure must fail the run, and the worker turns the exception into exit code 1 — there is no caller
/// a <c>Result</c> could inform.
/// </para>
/// </summary>
public static class IdentitySeed
{
    /// <summary>Creates missing roles and grants each role its missing permission claims; never removes anything.</summary>
    public static async Task EnsureRolesAndPermissionsAsync(RoleManager<IdentityRole<Guid>> roleManager)
    {
        ArgumentNullException.ThrowIfNull(roleManager);

        foreach ((string roleName, IReadOnlyList<string> permissions) in Roles.Permissions)
        {
            IdentityRole<Guid>? role = await roleManager.FindByNameAsync(roleName);
            if (role is null)
            {
                role = new IdentityRole<Guid>(roleName) { Id = Guid.CreateVersion7() };
                EnsureSucceeded(await roleManager.CreateAsync(role), $"Creating role '{roleName}'");
            }

            var granted = (await roleManager.GetClaimsAsync(role))
                .Where(static claim => string.Equals(claim.Type, AuthClaims.Permission, StringComparison.Ordinal))
                .Select(static claim => claim.Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (string permission in permissions.Where(permission => !granted.Contains(permission)))
            {
                EnsureSucceeded(
                    await roleManager.AddClaimAsync(role, new Claim(AuthClaims.Permission, permission)),
                    $"Granting '{permission}' to role '{roleName}'");
            }
        }
    }

    /// <summary>
    /// Creates the administrator if no account has <paramref name="email"/>, then makes sure it holds the
    /// Administrator role — so an earlier run that created the account but failed before the role
    /// assignment (an account that signs in with no permissions) is repaired by the next run. An existing
    /// account's password is never changed.
    /// </summary>
    public static async Task EnsureAdministratorAsync(
        UserManager<ApplicationUser> userManager,
        string email,
        string password
#if (UseMultitenancy)
        , string tenantId
#endif
        )
    {
        ArgumentNullException.ThrowIfNull(userManager);

        ApplicationUser? administrator = await userManager.FindByEmailAsync(email);
        if (administrator is null)
        {
            administrator = new ApplicationUser
            {
                Id = Guid.CreateVersion7(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
#if (UseMultitenancy)
                TenantId = tenantId,
#endif
            };
            EnsureSucceeded(await userManager.CreateAsync(administrator, password), $"Creating the administrator '{email}'");
        }

        if (!await userManager.IsInRoleAsync(administrator, Roles.Administrator))
        {
            EnsureSucceeded(
                await userManager.AddToRoleAsync(administrator, Roles.Administrator),
                $"Adding '{email}' to role '{Roles.Administrator}'");
        }
    }

    /// <summary>
    /// Throws when <paramref name="result"/> failed, naming the step (<paramref name="what"/>) and every
    /// Identity error with its code, e.g. <c>Creating the administrator 'a@b.c' failed: [PasswordRequiresDigit]
    /// Passwords must have at least one digit ('0'-'9').</c>
    /// </summary>
    public static void EnsureSucceeded(IdentityResult result, string what)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.Succeeded)
        {
            return;
        }

        string errors = string.Join(" ", result.Errors.Select(static error => $"[{error.Code}] {error.Description}"));
        throw new InvalidOperationException($"{what} failed: {(errors.Length > 0 ? errors : "Identity reported no error detail.")}");
    }
}
