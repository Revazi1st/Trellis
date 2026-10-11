using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Trellis.Infrastructure.Email;

/// <summary>
/// Checks <see cref="SmtpOptions"/> when, and only when, SMTP is the selected provider: the annotations
/// on its properties plus the rules that span settings or need the mail parser. It runs wherever the
/// relay is validated — the SMTP sender's first use in every host, and boot in the host that sends — so
/// a relay misconfiguration stops the API's rollout, not the first receipt.
/// </summary>
internal sealed class SmtpOptionsValidator(IOptions<EmailOptions> email) : IValidateOptions<SmtpOptions>
{
    public ValidateOptionsResult Validate(string? name, SmtpOptions options)
    {
        if (SelectedProvider() != EmailProvider.Smtp)
        {
            return ValidateOptionsResult.Skip;
        }

        var annotations = new List<ValidationResult>();
        Validator.TryValidateObject(options, new ValidationContext(options), annotations, validateAllProperties: true);
        List<string> failures = [.. annotations.Select(static result => result.ErrorMessage ?? "Email:Smtp is invalid.")];

        if (options.Port == SmtpOptions.ImplicitTlsPort)
        {
            failures.Add("Email:Smtp:Port 465 is implicit TLS (SMTPS), which System.Net.Mail cannot speak. "
                + "Use the relay's STARTTLS submission port, normally 587.");
        }

        if (!string.IsNullOrWhiteSpace(options.FromAddress) && !MailAddress.TryCreate(options.FromAddress, out _))
        {
            failures.Add("Email:Smtp:FromAddress is not an e-mail address: write billing@example.com, "
                + "or Acme Billing <billing@example.com> for a display name.");
        }

        bool hasUserName = !string.IsNullOrEmpty(options.UserName);
        bool hasPassword = !string.IsNullOrEmpty(options.Password);
        if (hasUserName != hasPassword)
        {
            failures.Add("Email:Smtp:UserName and Email:Smtp:Password go together: set both, "
                + "or neither for a relay that accepts unauthenticated submission.");
        }

        if ((hasUserName || hasPassword) && !options.UseStartTls)
        {
            failures.Add("Email:Smtp:UseStartTls=false would send the relay credentials in clear. Plaintext is "
                + "for a local mail catcher without credentials only: remove the credentials or turn STARTTLS back on.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    /// <summary>
    /// The provider, or <see langword="null"/> when the "Email" section is itself invalid: that section
    /// reports its own failure, and repeating it here would only say the same thing twice.
    /// </summary>
    private EmailProvider? SelectedProvider()
    {
        try
        {
            return email.Value.Provider;
        }
        catch (OptionsValidationException)
        {
            return null;
        }
    }
}
