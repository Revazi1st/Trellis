using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Application.Abstractions;
using Trellis.Application.Authorization;
using Trellis.Application.Features.Auth;
using Trellis.Persistence.Identity;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// Two registrations of one e-mail racing each other. Both pass the up-front lookup and Identity's own
/// uniqueness check (a rendezvous holds each until the other has validated too), so the database's unique
/// user-name index decides: the loser is answered 409 <c>Auth.EmailAlreadyInUse</c> — what a sequential
/// duplicate gets — never a 500, and exactly one account, with its role, exists.
/// </summary>
public sealed class RegistrationRaceTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    private const string Password = "Registr4tion-race!";

    private readonly Rendezvous _validated = new();

    protected override void ConfigureServices(IServiceCollection services)
        // Registered after Identity's own UserValidator, so a registration arrives here having passed every
        // uniqueness check Identity makes.
        => services.AddSingleton<IUserValidator<ApplicationUser>>(_validated);

    [Fact]
    public async Task Two_concurrent_registrations_of_one_email_create_one_account_and_answer_the_loser_409()
    {
        const string email = "racer@registration.test";
        await IdentitySeed.EnsureRolesAndPermissionsAsync(Services.GetRequiredService<RoleManager<IdentityRole<Guid>>>());
        _validated.Expect(parties: 2);

        Result<Guid>[] outcomes = await Task.WhenAll(RegisterInOwnScopeAsync(email), RegisterInOwnScopeAsync(email));

        Result<Guid> winner = outcomes.Where(static outcome => outcome.IsSuccess).ShouldHaveSingleItem();
        outcomes.Where(static outcome => outcome.IsFailure).ShouldHaveSingleItem().Error.ShouldBe(AuthErrors.EmailAlreadyInUse);

        using IServiceScope scope = CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        ApplicationUser account = (await users.FindByEmailAsync(email)).ShouldNotBeNull();
        account.Id.ShouldBe(winner.Value);
        (await users.GetRolesAsync(account)).ShouldBe([Roles.Member]);

        // Afterwards the ordinary path answers the same: the up-front lookup finds the account.
        (await RegisterInOwnScopeAsync(email)).Error.ShouldBe(AuthErrors.EmailAlreadyInUse);
    }

    private async Task<Result<Guid>> RegisterInOwnScopeAsync(string email)
    {
        await Task.Yield();   // both registrations are under way before either reaches the database
        using IServiceScope scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IIdentityService>()
            .RegisterAsync(email, Password, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A user validator that holds each caller until <see cref="Expect"/>'s number of callers have arrived,
    /// then lets everyone through, later callers too (the role assignment validates the user again).
    /// Unarmed, it passes straight through.
    /// </summary>
    private sealed class Rendezvous : IUserValidator<ApplicationUser>
    {
        private readonly TaskCompletionSource _allArrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _parties;
        private int _arrived;

        public void Expect(int parties) => _parties = parties;

        public async Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user)
        {
            if (_parties > 0)
            {
                if (Interlocked.Increment(ref _arrived) >= _parties)
                {
                    _allArrived.TrySetResult();
                }

                await _allArrived.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            }

            return IdentityResult.Success;
        }
    }
}
