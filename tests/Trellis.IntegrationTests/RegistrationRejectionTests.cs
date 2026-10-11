using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Application.Abstractions;
using Trellis.Application.Features.Auth;
using Trellis.Persistence.Identity;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// What a registration Identity refuses answers: one code per error type — 400 <c>Auth.RegistrationRejected</c>
/// for anything the caller can fix, 409 <c>Auth.EmailAlreadyInUse</c> for a duplicate — and a description made
/// of fixed sentences keyed by Identity's error codes, never Identity's own text, which echoes the e-mail.
/// </summary>
public sealed class RegistrationRejectionTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    [Fact]
    public async Task A_password_identity_rejects_is_a_validation_error_naming_each_rule()
    {
        const string email = "weak-password@registration.test";

        // Long enough for the validator's floor, so it reaches Identity — which also wants an upper-case
        // letter and a symbol.
        Result<Guid> registered = await RegisterAsync(email, "password1");

        Error error = registered.Error.ShouldNotBeNull();
        error.Type.ShouldBe(ErrorType.Validation);
        error.Code.ShouldBe(RegistrationErrors.RejectedCode);
        error.Description.ShouldBe(
            "The password must contain a character that is neither a letter nor a digit. The password must contain an upper-case letter.");
        (await FindAsync(email)).ShouldBeNull();
    }

    [Fact]
    public async Task An_email_identity_rejects_is_never_echoed_back()
    {
        // FluentValidation's e-mail rule lets the apostrophe through; Identity's user-name alphabet does not,
        // and its own description would read "Username 'o'brien@…' is invalid".
        const string email = "o'brien@registration.test";

        Result<Guid> registered = await RegisterAsync(email, "Registr4tion-pass!");

        Error error = registered.Error.ShouldNotBeNull();
        error.Code.ShouldBe(RegistrationErrors.RejectedCode);
        error.Description.ShouldBe("The email address cannot be used for an account.");
        error.Description.ShouldNotContain("brien");
    }

    [Theory]
    [InlineData(nameof(IdentityErrorDescriber.DuplicateUserName))]
    [InlineData(nameof(IdentityErrorDescriber.DuplicateEmail))]
    public void A_duplicate_identity_reports_is_the_409_a_sequential_duplicate_gets(string code)
    {
        IdentityResult rejected = IdentityResult.Failed(
            new IdentityError { Code = code, Description = "Username 'jane@example.com' is already taken." });

        RegistrationErrors.From(rejected, new PasswordOptions()).ShouldBe(AuthErrors.EmailAlreadyInUse);
    }

    [Fact]
    public void Unknown_codes_get_one_generic_sentence_and_lengths_come_from_the_options()
    {
        IdentityResult rejected = IdentityResult.Failed(
            new IdentityError { Code = "SomethingNew", Description = "jane@example.com said no." },
            new IdentityError { Code = "AnotherNewOne", Description = "jane@example.com said no again." },
            new IdentityError { Code = nameof(IdentityErrorDescriber.PasswordTooShort), Description = "Passwords must be at least 12 characters." });

        Error error = RegistrationErrors.From(rejected, new PasswordOptions { RequiredLength = 12 });

        error.ShouldBe(Error.Validation(
            RegistrationErrors.RejectedCode,
            "The account could not be created with these details. The password must be at least 12 characters long."));
    }

    private async Task<Result<Guid>> RegisterAsync(string email, string password)
    {
        using IServiceScope scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IIdentityService>()
            .RegisterAsync(email, password, TestContext.Current.CancellationToken);
    }

    private async Task<ApplicationUser?> FindAsync(string email)
    {
        using IServiceScope scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>().FindByEmailAsync(email);
    }
}
