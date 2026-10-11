using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Trellis.Application.Features.Auth;
using Trellis.SharedKernel.Results;

namespace Trellis.Persistence.Identity;

/// <summary>
/// Turns the <see cref="IdentityResult"/> of a rejected account creation into the registration's
/// <see cref="Result"/> error. Identity's own descriptions are never passed on: several echo the e-mail
/// ("Username 'jane@example.com' is already taken."), which would then sit in the response and in the
/// use-case log line. Each <see cref="IdentityError.Code"/> maps to a fixed sentence instead.
/// </summary>
internal static class RegistrationErrors
{
    /// <summary>One code for every rejection the caller can fix by changing the request: always a 400.</summary>
    public const string RejectedCode = "Auth.RegistrationRejected";

    private const string GenericSentence = "The account could not be created with these details.";

    /// <summary>
    /// A duplicate (Identity saw the e-mail already registered) is <see cref="AuthErrors.EmailAlreadyInUse"/>,
    /// the 409 the up-front lookup answers. Anything else is a <see cref="RejectedCode"/> validation error
    /// whose description lists what to change, one fixed sentence per Identity code.
    /// </summary>
    public static Error From(IdentityResult result, PasswordOptions password)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(password);

        if (result.Errors.Any(static error => error.Code is nameof(IdentityErrorDescriber.DuplicateUserName) or nameof(IdentityErrorDescriber.DuplicateEmail)))
        {
            return AuthErrors.EmailAlreadyInUse;
        }

        IEnumerable<string> sentences = result.Errors
            .Select(error => SentenceFor(error.Code, password))
            .Distinct(StringComparer.Ordinal);

        string description = string.Join(" ", sentences);
        return Error.Validation(RejectedCode, description.Length > 0 ? description : GenericSentence);
    }

    private static string SentenceFor(string code, PasswordOptions password) => code switch
    {
        nameof(IdentityErrorDescriber.PasswordTooShort) => string.Create(
            CultureInfo.InvariantCulture, $"The password must be at least {password.RequiredLength} characters long."),
        nameof(IdentityErrorDescriber.PasswordRequiresUniqueChars) => string.Create(
            CultureInfo.InvariantCulture, $"The password must contain at least {password.RequiredUniqueChars} different characters."),
        nameof(IdentityErrorDescriber.PasswordRequiresDigit) => "The password must contain a digit.",
        nameof(IdentityErrorDescriber.PasswordRequiresLower) => "The password must contain a lower-case letter.",
        nameof(IdentityErrorDescriber.PasswordRequiresUpper) => "The password must contain an upper-case letter.",
        nameof(IdentityErrorDescriber.PasswordRequiresNonAlphanumeric) => "The password must contain a character that is neither a letter nor a digit.",
        nameof(IdentityErrorDescriber.InvalidEmail) or nameof(IdentityErrorDescriber.InvalidUserName) => "The email address cannot be used for an account.",
        _ => GenericSentence,
    };
}
