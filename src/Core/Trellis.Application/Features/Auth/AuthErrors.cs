using Trellis.SharedKernel.Results;

namespace Trellis.Application.Features.Auth;

public static class AuthErrors
{
    public static readonly Error InvalidCredentials =
        Error.Unauthorized("Auth.InvalidCredentials", "The email or password is incorrect.");

    public static readonly Error InvalidRefreshToken =
        Error.Unauthorized("Auth.InvalidRefreshToken", "The refresh token is invalid or has expired.");

    public static readonly Error EmailAlreadyInUse =
        Error.Conflict("Auth.EmailAlreadyInUse", "An account with this email already exists.");

    public static readonly Error AccountLockedOut =
        Error.Forbidden("Auth.AccountLockedOut", "The account is temporarily locked after too many failed sign-in attempts. Try again later.");

    public static readonly Error RegistrationClosed =
        Error.Forbidden("Auth.RegistrationClosed", "Self-registration is closed. Ask an administrator to create your account.");
}
