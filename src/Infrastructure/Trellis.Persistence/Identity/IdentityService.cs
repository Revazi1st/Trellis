using Microsoft.AspNetCore.Identity;
using Trellis.Application.Abstractions;
using Trellis.Application.Authorization;
using Trellis.Application.Features.Auth;
using Trellis.SharedKernel.Results;

namespace Trellis.Persistence.Identity;

/// <summary>ASP.NET Core Identity-backed implementation of the application's identity port.</summary>
internal sealed class IdentityService(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager) : IIdentityService
{
    public async Task<Result<Guid>> RegisterAsync(string email, string password, CancellationToken cancellationToken)
    {
        if (await userManager.FindByEmailAsync(email) is not null)
        {
            return Result.Failure<Guid>(AuthErrors.EmailAlreadyInUse);
        }

        var user = new ApplicationUser { UserName = email, Email = email };
        IdentityResult result = await userManager.CreateAsync(user, password);

        if (!result.Succeeded)
        {
            return Result.Failure<Guid>(Error.Validation(
                "Auth.RegistrationFailed",
                string.Join(" ", result.Errors.Select(static e => e.Description))));
        }

        await userManager.AddToRoleAsync(user, Roles.Member);

        return user.Id;
    }

    public async Task<Result<AuthenticatedUser>> ValidateCredentialsAsync(string email, string password, CancellationToken cancellationToken)
    {
        ApplicationUser? user = await userManager.FindByEmailAsync(email);

        if (user is null)
        {
            return Result.Failure<AuthenticatedUser>(AuthErrors.InvalidCredentials);
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            return Result.Failure<AuthenticatedUser>(AuthErrors.AccountLockedOut);
        }

        if (!await userManager.CheckPasswordAsync(user, password))
        {
            // Counts toward lockout; Identity locks the account at the configured threshold.
            await userManager.AccessFailedAsync(user);
            return Result.Failure<AuthenticatedUser>(await userManager.IsLockedOutAsync(user)
                ? AuthErrors.AccountLockedOut
                : AuthErrors.InvalidCredentials);
        }

        await userManager.ResetAccessFailedCountAsync(user);
        return await BuildAuthenticatedUserAsync(user);
    }

    public async Task<Result<AuthenticatedUser>> GetUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        ApplicationUser? user = await userManager.FindByIdAsync(userId.ToString());

        return user is null
            ? Result.Failure<AuthenticatedUser>(AuthErrors.InvalidCredentials)
            : await BuildAuthenticatedUserAsync(user);
    }

    /// <summary>Effective permissions = the union of the "permission" claims across all the user's roles.</summary>
    private async Task<AuthenticatedUser> BuildAuthenticatedUserAsync(ApplicationUser user)
    {
        var permissions = new HashSet<string>(StringComparer.Ordinal);

        foreach (string roleName in await userManager.GetRolesAsync(user))
        {
            if (await roleManager.FindByNameAsync(roleName) is { } role)
            {
                IEnumerable<string> rolePermissions = (await roleManager.GetClaimsAsync(role))
                    .Where(static claim => string.Equals(claim.Type, AuthClaims.Permission, StringComparison.Ordinal))
                    .Select(static claim => claim.Value);

                foreach (string permission in rolePermissions)
                {
                    permissions.Add(permission);
                }
            }
        }

        return new AuthenticatedUser(user.Id, user.Email!, [.. permissions]);
    }
}
