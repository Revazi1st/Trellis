using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Trellis.Application.Abstractions;
using Trellis.Application.Features.Auth;
using Trellis.Persistence;
using Trellis.Persistence.Identity;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// The lockout counter's two writes report failure as an <see cref="IdentityResult"/>, and neither is
/// discarded: a failed sign-in Identity could not count is security event 1105, a count it could not reset
/// after a good sign-in is 1106 — both at Warning, neither changing what the caller is told.
/// </summary>
public sealed class LockoutBookkeepingTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    private const string Email = "bookkeeping@lockout.test";
    private const string Password = "L0ckout-books!";

    [Fact]
    public async Task A_failed_sign_in_identity_cannot_count_is_still_a_401_and_logs_event_1105()
    {
        await RegisterAsync();
        var log = new CapturingLogger();

        Result<AuthenticatedUser> signedIn = await SignInAsync(log, "wrong-password");

        signedIn.Error.ShouldBe(AuthErrors.InvalidCredentials);
        CapturedEntry entry = log.Entries.Where(static e => e.EventId.Id == 1105).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain(nameof(IdentityErrorDescriber.ConcurrencyFailure));
        entry.Message.ShouldNotContain(Email);
    }

    [Fact]
    public async Task A_count_identity_cannot_reset_leaves_the_sign_in_standing_and_logs_event_1106()
    {
        await RegisterAsync();
        var log = new CapturingLogger();

        Result<AuthenticatedUser> signedIn = await SignInAsync(log, Password);

        signedIn.ShouldBeSuccess();
        CapturedEntry entry = log.Entries.Where(static e => e.EventId.Id == 1106).ShouldHaveSingleItem();
        entry.Level.ShouldBe(LogLevel.Warning);
        entry.Message.ShouldContain(nameof(IdentityErrorDescriber.ConcurrencyFailure));
    }

    private async Task RegisterAsync()
    {
        await IdentitySeed.EnsureRolesAndPermissionsAsync(Services.GetRequiredService<RoleManager<IdentityRole<Guid>>>());
        (await Services.GetRequiredService<IIdentityService>().RegisterAsync(Email, Password, TestContext.Current.CancellationToken))
            .ShouldBeSuccess();
    }

    private async Task<Result<AuthenticatedUser>> SignInAsync(ILogger<IdentityService> logger, string password)
    {
        using IServiceScope scope = CreateScope();
        IServiceProvider services = scope.ServiceProvider;
        var identityService = new IdentityService(
            services.GetRequiredService<AppDbContext>(),
            CountRefusingUserManager.From(services),
            services.GetRequiredService<RoleManager<IdentityRole<Guid>>>(),
            logger);

        return await identityService.ValidateCredentialsAsync(Email, password, TestContext.Current.CancellationToken);
    }

    private sealed record CapturedEntry(LogLevel Level, EventId EventId, string Message);

    private sealed class CapturingLogger : ILogger<IdentityService>
    {
        public List<CapturedEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new CapturedEntry(logLevel, eventId, formatter(state, exception)));
    }

    /// <summary>The real UserManager except that both lockout-counter writes report a concurrency failure, as a lost update would.</summary>
    private sealed class CountRefusingUserManager(
        IUserStore<ApplicationUser> store,
        IOptions<IdentityOptions> options,
        IPasswordHasher<ApplicationUser> passwordHasher,
        IEnumerable<IUserValidator<ApplicationUser>> userValidators,
        IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators,
        ILookupNormalizer keyNormalizer,
        IdentityErrorDescriber errors,
        IServiceProvider services)
        : UserManager<ApplicationUser>(store, options, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, NullLogger<UserManager<ApplicationUser>>.Instance)
    {
        public static CountRefusingUserManager From(IServiceProvider services)
            => new(
                services.GetRequiredService<IUserStore<ApplicationUser>>(),
                services.GetRequiredService<IOptions<IdentityOptions>>(),
                services.GetRequiredService<IPasswordHasher<ApplicationUser>>(),
                services.GetServices<IUserValidator<ApplicationUser>>(),
                services.GetServices<IPasswordValidator<ApplicationUser>>(),
                services.GetRequiredService<ILookupNormalizer>(),
                services.GetRequiredService<IdentityErrorDescriber>(),
                services);

        public override Task<IdentityResult> AccessFailedAsync(ApplicationUser user) => Refused();

        public override Task<IdentityResult> ResetAccessFailedCountAsync(ApplicationUser user) => Refused();

        private Task<IdentityResult> Refused() => Task.FromResult(IdentityResult.Failed(ErrorDescriber.ConcurrencyFailure()));
    }
}
