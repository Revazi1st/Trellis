using FluentValidation;

namespace Trellis.Application.Features.Auth.Login;

/// <summary>
/// Shape only: no e-mail format rule and no password policy here — login must not tell a caller
/// which rules an account's credentials follow. It only keeps empty and oversized input away from
/// Identity and the password hasher.
/// </summary>
internal sealed class LoginCommandValidator : AbstractValidator<LoginCommand>
{
    public LoginCommandValidator()
    {
        RuleFor(static c => c.Email).NotEmpty().MaximumLength(AuthInputLimits.EmailMaxLength);
        RuleFor(static c => c.Password).NotEmpty().MaximumLength(AuthInputLimits.PasswordMaxLength);
    }
}
