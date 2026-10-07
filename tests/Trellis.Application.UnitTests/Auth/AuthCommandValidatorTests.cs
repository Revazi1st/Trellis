using System.Security.Cryptography;
using FluentValidation.Results;
using Shouldly;
using Trellis.Application.Features.Auth.Login;
using Trellis.Application.Features.Auth.Logout;
using Trellis.Application.Features.Auth.RefreshToken;
using Xunit;

namespace Trellis.Application.UnitTests.Auth;

/// <summary>
/// The shape rules that keep null, empty and oversized auth input away from Identity, the password
/// hasher and the refresh-token hash — each of which turned such input into a 500 before.
/// </summary>
public sealed class AuthCommandValidatorTests
{
    private static readonly string LongestEmail = new string('a', 320 - "@acme.com".Length) + "@acme.com";

    // The issued format (JwtTokenProvider.CreateRefreshToken): 32 random bytes, base64url without padding.
    private static readonly string IssuedRefreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public void Login_ShapedInput_IsValid()
    {
        Validate(new LoginCommand("member@acme.com", "anything at all")).IsValid.ShouldBeTrue();
        Validate(new LoginCommand(LongestEmail, new string('p', 128))).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null, "pw", "Email")]
    [InlineData("", "pw", "Email")]
    [InlineData("member@acme.com", null, "Password")]
    [InlineData("member@acme.com", "", "Password")]
    public void Login_MissingField_IsRejected(string? email, string? password, string field)
    {
        ValidationResult result = Validate(new LoginCommand(email!, password!));

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain(error => error.PropertyName == field);
    }

    [Fact]
    public void Login_OversizedInput_IsRejected()
    {
        Validate(new LoginCommand("x" + LongestEmail, "pw")).Errors
            .ShouldContain(static error => error.PropertyName == nameof(LoginCommand.Email));
        Validate(new LoginCommand("member@acme.com", new string('p', 129))).Errors
            .ShouldContain(static error => error.PropertyName == nameof(LoginCommand.Password));
    }

    [Fact]
    public void Login_DoesNotJudgeTheEmailFormat()
    {
        // Login answers 401 for anything that is not an account; a format rule would answer 400 and
        // tell the caller something about how accounts are named.
        Validate(new LoginCommand("not-an-email", "pw")).IsValid.ShouldBeTrue();
    }

    [Fact]
    public void RefreshAndLogout_AcceptTheIssuedTokenFormat()
    {
        IssuedRefreshToken.Length.ShouldBe(43);
        Validate(new RefreshTokenCommand(IssuedRefreshToken)).IsValid.ShouldBeTrue();
        Validate(new LogoutCommand(IssuedRefreshToken)).IsValid.ShouldBeTrue();
        Validate(new RefreshTokenCommand(new string('t', 512))).IsValid.ShouldBeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void RefreshAndLogout_MissingToken_IsRejected(string? token)
    {
        Validate(new RefreshTokenCommand(token!)).IsValid.ShouldBeFalse();
        Validate(new LogoutCommand(token!)).IsValid.ShouldBeFalse();
    }

    [Fact]
    public void RefreshAndLogout_OversizedToken_IsRejected()
    {
        string oversized = new('t', 513);

        Validate(new RefreshTokenCommand(oversized)).IsValid.ShouldBeFalse();
        Validate(new LogoutCommand(oversized)).IsValid.ShouldBeFalse();
    }

    private static ValidationResult Validate(LoginCommand command) => new LoginCommandValidator().Validate(command);

    private static ValidationResult Validate(RefreshTokenCommand command) => new RefreshTokenCommandValidator().Validate(command);

    private static ValidationResult Validate(LogoutCommand command) => new LogoutCommandValidator().Validate(command);
}
