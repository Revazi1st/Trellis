using Trellis.SharedKernel.Results;

namespace Trellis.Domain.Common;

/// <summary>Validated email address value object; construct via <see cref="Create"/>.</summary>
public sealed record EmailAddress
{
    private EmailAddress(string value) => Value = value;

    public string Value { get; }

    public static Result<EmailAddress> Create(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return EmailAddressErrors.Empty;
        }

        string trimmed = value.Trim();
        int atIndex = trimmed.IndexOf('@', StringComparison.Ordinal);

        // Deliberately shallow: real deliverability is proven by sending mail, not by regex.
        bool shapeIsValid = atIndex > 0
            && atIndex < trimmed.Length - 1
            && trimmed.IndexOf('@', atIndex + 1) < 0
            && trimmed.IndexOf('.', atIndex) > atIndex + 1
            && !trimmed.Contains(' ', StringComparison.Ordinal);

        return shapeIsValid ? new EmailAddress(trimmed) : EmailAddressErrors.Invalid;
    }

    public override string ToString() => Value;
}
