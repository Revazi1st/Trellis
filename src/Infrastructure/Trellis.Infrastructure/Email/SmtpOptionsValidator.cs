using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Trellis.Infrastructure.Email;

/// <summary>
/// Checks <see cref="EmailOptions.Smtp"/> when, and only when, SMTP is the selected provider: the
/// annotations on <see cref="SmtpOptions"/> plus the rules that span settings or need the mail parser.
/// It runs wherever <see cref="EmailOptions"/> is validated — the sender's first use in every host, and
/// boot in the host that sends — so a relay misconfiguration stops the API's rollout, not the first receipt.
/// </summary>
internal sealed class SmtpOptionsValidator : IValidateOptions<EmailOptions>
{
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        if (options.Provider != EmailProvider.Smtp)
        {
            return ValidateOptionsResult.Skip;
        }

        SmtpOptions smtp = options.Smtp;
        var annotations = new List<ValidationResult>();
        Validator.TryValidateObject(smtp, new ValidationContext(smtp), annotations, validateAllProperties: true);
        List<string> failures = [.. annotations.Select(static result => result.ErrorMessage ?? "Email:Smtp is invalid.")];

        if (smtp.Port == SmtpOptions.ImplicitTlsPort)
        {
            failures.Add("Email:Smtp:Port 465 is implicit TLS (SMTPS), which System.Net.Mail cannot speak. "
                + "Use the relay's STARTTLS submission port, normally 587.");
        }

        if (!string.IsNullOrWhiteSpace(smtp.FromAddress) && !MailAddress.TryCreate(smtp.FromAddress, out _))
        {
            failures.Add("Email:Smtp:FromAddress is not an e-mail address: write billing@example.com, "
                + "or Acme Billing <billing@example.com> for a display name.");
        }

        bool hasUserName = !string.IsNullOrEmpty(smtp.UserName);
        bool hasPassword = !string.IsNullOrEmpty(smtp.Password);
        if (hasUserName != hasPassword)
        {
            failures.Add("Email:Smtp:UserName and Email:Smtp:Password go together: set both, "
                + "or neither for a relay that accepts unauthenticated submission.");
        }

        if ((hasUserName || hasPassword) && !smtp.UseStartTls)
        {
            failures.Add("Email:Smtp:UseStartTls=false would send the relay credentials in clear. Plaintext is "
                + "for a local mail catcher without credentials only: remove the credentials or turn STARTTLS back on.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
