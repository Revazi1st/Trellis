using FluentValidation;

namespace XTemplate.Application.Features.Auth.Register;

internal sealed class RegisterCommandValidator : AbstractValidator<RegisterCommand>
{
    public RegisterCommandValidator()
    {
        RuleFor(static c => c.Email).NotEmpty().EmailAddress().MaximumLength(320);
        RuleFor(static c => c.Password).NotEmpty().MinimumLength(8).MaximumLength(128);
    }
}
