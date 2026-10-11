using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Trellis.Application.Abstractions;
using Trellis.Application.Authorization;
using Trellis.Persistence;
using Trellis.Persistence.Identity;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// <see cref="IIdentityService.RegisterAsync"/> creates the account and its Member role as one unit: when
/// the role cannot be assigned — reported as a failed <see cref="IdentityResult"/> or thrown — no
/// role-less account remains, and the e-mail is free to register again.
/// </summary>
public sealed class RegistrationAtomicityTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    private const string Password = "Registr4tion-pass!";

    [Fact]
    public async Task A_registered_account_holds_the_member_role()
    {
        await IdentitySeed.EnsureRolesAndPermissionsAsync(Services.GetRequiredService<RoleManager<IdentityRole<Guid>>>());

        Result<Guid> registered = await Services.GetRequiredService<IIdentityService>()
            .RegisterAsync("member@registration.test", Password, TestContext.Current.CancellationToken);

        registered.ShouldBeSuccess();
        using IServiceScope scope = CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser user = (await users.FindByIdAsync(registered.Value.ToString())).ShouldNotBeNull();
        (await users.GetRolesAsync(user)).ShouldBe([Roles.Member]);
    }

    [Fact]
    public async Task A_rejected_role_assignment_is_a_failure_result_and_leaves_no_account()
    {
        const string email = "rejected-role@registration.test";
        await IdentitySeed.EnsureRolesAndPermissionsAsync(Services.GetRequiredService<RoleManager<IdentityRole<Guid>>>());

        Result<Guid> registered;
        using (IServiceScope scope = CreateScope())
        {
            IServiceProvider services = scope.ServiceProvider;
            var identityService = new IdentityService(
                services.GetRequiredService<AppDbContext>(),
                RoleRejectingUserManager.From(services),
                services.GetRequiredService<RoleManager<IdentityRole<Guid>>>(),
                NullLogger<IdentityService>.Instance);

            registered = await identityService.RegisterAsync(email, Password, TestContext.Current.CancellationToken);
        }

        registered.IsFailure.ShouldBeTrue();
        registered.Error.Type.ShouldBe(ErrorType.Failure);
        registered.Error.Code.ShouldBe("Auth.RegistrationFailed");
        (await FindAsync(email)).ShouldBeNull("the account was created in the same transaction and must be rolled back");

        // The e-mail is not burned: the same registration succeeds once the role can be assigned.
        Result<Guid> retried = await Services.GetRequiredService<IIdentityService>()
            .RegisterAsync(email, Password, TestContext.Current.CancellationToken);
        retried.ShouldBeSuccess();
    }

    [Fact]
    public async Task A_role_assignment_that_throws_leaves_no_account()
    {
        // No roles seeded: UserManager.AddToRoleAsync throws for a role that does not exist.
        const string email = "missing-role@registration.test";

        await Should.ThrowAsync<InvalidOperationException>(() => Services.GetRequiredService<IIdentityService>()
            .RegisterAsync(email, Password, TestContext.Current.CancellationToken));

        (await FindAsync(email)).ShouldBeNull();
    }

    private async Task<ApplicationUser?> FindAsync(string email)
    {
        using IServiceScope scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
    }

    /// <summary>The real UserManager (store, validators, hasher) except that every role assignment is refused.</summary>
    private sealed class RoleRejectingUserManager(
        IUserStore<ApplicationUser> store,
        IOptions<IdentityOptions> options,
        IPasswordHasher<ApplicationUser> passwordHasher,
        IEnumerable<IUserValidator<ApplicationUser>> userValidators,
        IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators,
        ILookupNormalizer keyNormalizer,
        IdentityErrorDescriber errors,
        IServiceProvider services,
        ILogger<UserManager<ApplicationUser>> logger)
        : UserManager<ApplicationUser>(store, options, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
    {
        public static RoleRejectingUserManager From(IServiceProvider services)
            => new(
                services.GetRequiredService<IUserStore<ApplicationUser>>(),
                services.GetRequiredService<IOptions<IdentityOptions>>(),
                services.GetRequiredService<IPasswordHasher<ApplicationUser>>(),
                services.GetServices<IUserValidator<ApplicationUser>>(),
                services.GetServices<IPasswordValidator<ApplicationUser>>(),
                services.GetRequiredService<ILookupNormalizer>(),
                services.GetRequiredService<IdentityErrorDescriber>(),
                services,
                NullLogger<UserManager<ApplicationUser>>.Instance);

        public override Task<IdentityResult> AddToRoleAsync(ApplicationUser user, string role)
            => Task.FromResult(IdentityResult.Failed(new IdentityError { Code = "ConcurrencyFailure", Description = "Simulated." }));
    }
}
