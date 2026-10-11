using System.Globalization;
using Trellis.SharedKernel.Results;

namespace Trellis.Domain.Common;

public static class EmailAddressErrors
{
    public static readonly Error Empty =
        Error.Validation("EmailAddress.Empty", "An email address is required.");

    public static readonly Error Invalid =
        Error.Validation("EmailAddress.Invalid", "The email address is not in a valid format.");

    public static readonly Error TooLong = Error.Validation(
        "EmailAddress.TooLong",
        string.Create(
            CultureInfo.InvariantCulture,
            $"An email address has at most {EmailAddress.MaxLength} characters, {EmailAddress.MaxLocalPartLength} of them before the @."));
}
