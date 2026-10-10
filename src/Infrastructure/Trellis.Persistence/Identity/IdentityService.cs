using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Trellis.Application.Abstractions;
using Trellis.Application.Authorization;
using Trellis.Application.Features.Auth;
using Trellis.SharedKernel.Results;

namespace Trellis.Persistence.Identity;

/// <summary>ASP.NET Core Identity-backed implementation of the application's identity port.</summary>
internal sealed class IdentityService(
    AppDbContext context,
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    ILogger<IdentityService> logger) : IIdentityService
{
    /// <summary>The account was created but could not be given its role; both were rolled back.</summary>
    private static readonly Error RoleAssignmentFailed = Error.Failure(
        "Auth.RegistrationFailed",
        "The account could not be set up, so nothing was created. Try again later.");

    /// <summary>
    /// Creates the account and gives it the Member role as one unit. UserManager saves after each call,
    /// so on its own a failed role assignment would leave an account that signs in with no permissions
    /// and whose e-mail can never register again; inside one transaction a failure — a reported
    /// <see cref="IdentityResult"/> or an exception — rolls the account back with it. The transaction
    /// runs inside the execution strategy because the retrying strategy refuses a user-initiated one
    /// otherwise; a retried attempt starts from a clean change tracker.
    /// </summary>
    public async Task<Result<Guid>> RegisterAsync(string email, string password, CancellationToken cancellationToken)
    {
        if (await userManager.FindByEmailAsync(email) is not null)
        {
            return Result.Failure<Guid>(AuthErrors.EmailAlreadyInUse);
        }

        IExecutionStrategy strategy = context.Database.CreateExecutionStrategy();
        int attempt = 0;

        return await strategy.ExecuteAsync(
            async attemptToken =>
            {
                if (attempt++ > 0)
                {
                    context.ChangeTracker.Clear();
                }

                return await CreateMemberAsync(email, password, attemptToken);
            },
            cancellationToken);
    }

    private async Task<Result<Guid>> CreateMemberAsync(string email, string password, CancellationToken cancellationToken)
    {
        await using IDbContextTransaction transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
#if (UseMultitenancy)
            // Self-registration provisions a fresh tenant; the registrant is its first user. Joining
            // an existing tenant is an invitation flow this template does not ship (ADR-0022).
            TenantId = Guid.CreateVersion7().ToString(),
#endif
        };
        IdentityResult created = await userManager.CreateAsync(user, password);

        if (!created.Succeeded)
        {
            return Result.Failure<Guid>(Error.Validation(
                "Auth.RegistrationFailed",
                string.Join(" ", created.Errors.Select(static e => e.Description))));
        }

        IdentityResult assigned = await userManager.AddToRoleAsync(user, Roles.Member);

        if (!assigned.Succeeded)
        {
            // The account goes with the role (CancellationToken.None: an abandoned rollback would leave the
            // row to the connection's fate). The tracked rows go too, so nothing later in this scope
            // mistakes them for persisted ones.
            await transaction.RollbackAsync(CancellationToken.None);
            context.ChangeTracker.Clear();
            logger.LogWarning(
                "Registration rolled back: assigning the {Role} role failed ({Errors})",
                Roles.Member,
                string.Join(", ", assigned.Errors.Select(static e => e.Code)));
            return Result.Failure<Guid>(RoleAssignmentFailed);
        }

        await transaction.CommitAsync(cancellationToken);
        return user.Id;
    }

    /// <summary>
    /// An unknown e-mail and a wrong password both answer <see cref="AuthErrors.InvalidCredentials"/>
    /// after the same password-hash work. A locked account answers <see cref="AuthErrors.AccountLockedOut"/>
    /// whatever password is supplied: the lockout check runs before the password check, so a 403 never
    /// confirms a password (docs/threat-model.md accepts that it confirms the account exists).
    /// </summary>
    public async Task<Result<AuthenticatedUser>> ValidateCredentialsAsync(string email, string password, CancellationToken cancellationToken)
    {
        ApplicationUser? user = await userManager.FindByEmailAsync(email);

        if (user is null)
        {
            DummyPasswordHash.Verify(userManager.PasswordHasher, password);
            SecurityEventLog.LoginFailedUnknownAccount(logger, email);
            return Result.Failure<AuthenticatedUser>(AuthErrors.InvalidCredentials);
        }

        if (await userManager.IsLockedOutAsync(user))
        {
            SecurityEventLog.LoginRejectedLockedOut(logger, user.Id);
            return Result.Failure<AuthenticatedUser>(AuthErrors.AccountLockedOut);
        }

        if (!await userManager.CheckPasswordAsync(user, password))
        {
            // Counts toward lockout; Identity locks the account at the configured threshold.
            await userManager.AccessFailedAsync(user);

            if (await userManager.IsLockedOutAsync(user))
            {
                SecurityEventLog.AccountLockedOut(logger, user.Id);
                return Result.Failure<AuthenticatedUser>(AuthErrors.AccountLockedOut);
            }

            SecurityEventLog.LoginFailed(logger, user.Id);
            return Result.Failure<AuthenticatedUser>(AuthErrors.InvalidCredentials);
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

        return new AuthenticatedUser(
            user.Id,
            user.Email!,
            [.. permissions]
#if (UseMultitenancy)
            , user.TenantId
#endif
            );
    }
}
