using FluentValidation;

namespace Trellis.Application.Features.Auth.Register;

internal sealed class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator()
    {
        RuleFor(static c => c.Email).NotEmpty().EmailAddress().MaximumLength(AuthInputLimits.EmailMaxLength);
        RuleFor(static c => c.Password).NotEmpty()
            .MinimumLength(AuthInputLimits.PasswordMinLength)
            .MaximumLength(AuthInputLimits.PasswordMaxLength);
    }
}
