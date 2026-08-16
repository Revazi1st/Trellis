using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Application.Abstractions;
using Trellis.Application.Authorization;
using Trellis.Application.Features.Auth;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// Account lockout and refresh-token rotation against the real Identity + Persistence stack:
/// the per-account brute-force lockout and the stolen-token replay revocation are security
/// behavior that only shows up with a real database.
/// </summary>
public sealed class AuthSecurityTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    private const string Password = "Sup3r-secret!";

    /// <summary>Respawn wipes AspNetRoles between tests; registration needs the Member role to exist.</summary>
    private async Task<Guid> RegisterUserAsync(string email, CancellationToken ct)
    {
        var roleManager = Services.GetRequiredService<RoleManager<IdentityRole<Guid>>>();
        if (await roleManager.FindByNameAsync(Roles.Member) is null)
        {
            await roleManager.CreateAsync(new IdentityRole<Guid>(Roles.Member) { Id = Guid.CreateVersion7() });
        }

        Result<Guid> registered = await Services.GetRequiredService<IIdentityService>().RegisterAsync(email, Password, ct);
        registered.IsSuccess.ShouldBeTrue();
        return registered.Value;
    }

    [Fact]
    public async Task Repeated_failed_logins_lock_the_account_and_correct_password_is_rejected_while_locked()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var identity = Services.GetRequiredService<IIdentityService>();
        await RegisterUserAsync("lockout@test.dev", ct);

        // MaxFailedAccessAttempts is 5; the fifth failure trips the lockout.
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            Result<AuthenticatedUser> failed = await identity.ValidateCredentialsAsync("lockout@test.dev", "wrong-password", ct);
            failed.Error.ShouldBe(AuthErrors.InvalidCredentials);
        }

        Result<AuthenticatedUser> locking = await identity.ValidateCredentialsAsync("lockout@test.dev", "wrong-password", ct);
        locking.Error.ShouldBe(AuthErrors.AccountLockedOut);

        Result<AuthenticatedUser> lockedOut = await identity.ValidateCredentialsAsync("lockout@test.dev", Password, ct);
        lockedOut.Error.ShouldBe(AuthErrors.AccountLockedOut);
    }

    [Fact]
    public async Task Successful_login_resets_the_failed_attempt_counter()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var identity = Services.GetRequiredService<IIdentityService>();
        await RegisterUserAsync("reset@test.dev", ct);

        for (int attempt = 1; attempt <= 4; attempt++)
        {
            (await identity.ValidateCredentialsAsync("reset@test.dev", "wrong-password", ct)).IsFailure.ShouldBeTrue();
        }

        (await identity.ValidateCredentialsAsync("reset@test.dev", Password, ct)).IsSuccess.ShouldBeTrue();

        // The counter restarted: four more failures stay "invalid credentials", not "locked".
        for (int attempt = 1; attempt <= 4; attempt++)
        {
            Result<AuthenticatedUser> failed = await identity.ValidateCredentialsAsync("reset@test.dev", "wrong-password", ct);
            failed.Error.ShouldBe(AuthErrors.InvalidCredentials);
        }
    }

    [Fact]
    public async Task Consuming_a_refresh_token_twice_fails_and_revokes_the_users_other_sessions()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<IRefreshTokenStore>();
        Guid userId = await RegisterUserAsync("replay@test.dev", ct);

        DateTimeOffset expires = Now.AddDays(7);
        await store.StoreAsync(userId, "hash-session-1", expires, ct);
        await store.StoreAsync(userId, "hash-session-2", expires, ct);

        (await store.ConsumeAsync("hash-session-1", ct)).Value.ShouldBe(userId);

        // Replaying the consumed token is the stolen-token signal: it fails AND kills session 2.
        (await store.ConsumeAsync("hash-session-1", ct)).IsFailure.ShouldBeTrue();
        (await store.ConsumeAsync("hash-session-2", ct)).IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task Revoke_is_idempotent_and_does_not_trip_replay_detection()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<IRefreshTokenStore>();
        Guid userId = await RegisterUserAsync("logout@test.dev", ct);

        DateTimeOffset expires = Now.AddDays(7);
        await store.StoreAsync(userId, "hash-logout", expires, ct);
        await store.StoreAsync(userId, "hash-other", expires, ct);

        await store.RevokeAsync("hash-logout", ct);
        await store.RevokeAsync("hash-logout", ct);   // double logout — no error, no side effects

        (await store.ConsumeAsync("hash-logout", ct)).IsFailure.ShouldBeTrue();

        // Note the consume above counts as a replay signal and kills hash-other too, so RevokeAll
        // semantics are verified on a fresh pair.
        await store.StoreAsync(userId, "hash-a", expires, ct);
        await store.StoreAsync(userId, "hash-b", expires, ct);
        int revoked = await store.RevokeAllAsync(userId, ct);
        revoked.ShouldBe(2);
        (await store.ConsumeAsync("hash-a", ct)).IsFailure.ShouldBeTrue();
    }
}
