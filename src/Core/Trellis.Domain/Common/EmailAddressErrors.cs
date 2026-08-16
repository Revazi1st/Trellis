using Trellis.SharedKernel.Results;

namespace Trellis.Domain.Common;

public static class EmailAddressErrors
{
    public static readonly Error Empty =
        Error.Validation("EmailAddress.Empty", "An email address is required.");

    public static readonly Error Invalid =
        Error.Validation("EmailAddress.Invalid", "The email address is not in a valid format.");
}
