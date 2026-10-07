using FluentValidation;

namespace Trellis.Application.Features.Auth.Logout;

internal sealed class LogoutCommandValidator : AbstractValidator<LogoutCommand>
{
    public LogoutCommandValidator()
        => RuleFor(static c => c.RefreshToken).NotEmpty().MaximumLength(AuthInputLimits.RefreshTokenMaxLength);
}
