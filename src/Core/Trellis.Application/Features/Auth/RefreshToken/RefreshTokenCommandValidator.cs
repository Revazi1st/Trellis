using FluentValidation;

namespace Trellis.Application.Features.Auth.RefreshToken;

internal sealed class RefreshTokenCommandValidator : AbstractValidator<RefreshTokenCommand>
{
    public RefreshTokenCommandValidator()
        => RuleFor(static c => c.RefreshToken).NotEmpty().MaximumLength(AuthInputLimits.RefreshTokenMaxLength);
}
