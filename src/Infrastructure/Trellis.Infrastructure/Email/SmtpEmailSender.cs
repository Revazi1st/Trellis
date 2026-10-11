using System.Globalization;
using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Trellis.Application.Abstractions;

namespace Trellis.Infrastructure.Email;

/// <summary>
/// <c>Email:Provider=Smtp</c>: submits each message to the relay in <see cref="SmtpOptions"/> with the
/// BCL's <see cref="SmtpClient"/>, upgraded with STARTTLS unless a local catcher was configured.
/// <para>
/// One client per message: <see cref="SmtpClient"/> cannot run two sends at once and pools nothing worth
/// keeping, and a fresh connection per message is what a relay expects from an application anyway.
/// </para>
/// <para>
/// A failure is thrown, not returned. The port returns <see cref="Task"/>, so a throw is its only failure
/// signal, and a relay outage is a fault rather than an expected outcome (ADR-0004): the outbox processor
/// that runs the event handler records it and retries with back-off. The relay's reply is dropped from
/// what is thrown, because a rejected recipient's reply usually quotes the full address and the outbox
/// writes the message into its row and the log; the status code and the masked recipient stay. Delivery
/// is at least once — a crash between the relay's acceptance and the outbox's checkpoint sends again.
/// </para>
/// <para>
/// Cancellation is the caller's: the token reaches <see cref="SmtpClient.SendMailAsync(MailMessage, CancellationToken)"/>,
/// and the outbox gives each message's handlers <c>Outbox:DispatchTimeout</c> (30 s).
/// </para>
/// </summary>
internal sealed partial class SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        SmtpOptions smtp = options.Value;

        using var message = new MailMessage(new MailAddress(smtp.FromAddress), new MailAddress(to))
        {
            Subject = subject,
            Body = body,
        };

        // UseDefaultCredentials stays at its default (false) and is never assigned: setting it clears
        // Credentials, so an initializer that ordered the two the wrong way would silently send no AUTH.
        // EnableSsl is STARTTLS here (SmtpClient has no implicit TLS). S5332 wants it constant true; it is
        // true unless the operator configured a local catcher, which SmtpOptionsValidator refuses
        // together with credentials and the API warns about at boot outside Development.
#pragma warning disable S5332
        using var client = new SmtpClient(smtp.Host, smtp.Port)
        {
            DeliveryMethod = SmtpDeliveryMethod.Network,
            EnableSsl = smtp.UseStartTls,
            Credentials = string.IsNullOrEmpty(smtp.UserName) ? null : new NetworkCredential(smtp.UserName, smtp.Password),
        };
#pragma warning restore S5332

        try
        {
            await client.SendMailAsync(message, cancellationToken);
        }
        catch (SmtpException exception)
        {
            throw Redacted(exception, to, smtp);
        }

        // Masking allocates; skip it when nobody listens.
        if (logger.IsEnabled(LogLevel.Information))
        {
            string maskedRecipient = EmailAddressMask.Mask(to);
            EmailSent(logger, maskedRecipient, subject, body.Length);
        }
    }

    /// <summary>
    /// The same failure without the relay's reply: status code, masked recipient and relay endpoint, plus
    /// the transport fault underneath (a refused connection, a failed TLS handshake), which names no
    /// recipient. A nested SMTP exception is dropped for the reason its parent is.
    /// </summary>
    private static SmtpException Redacted(SmtpException exception, string to, SmtpOptions smtp)
    {
        string message = string.Create(CultureInfo.InvariantCulture,
            $"SMTP submission to {EmailAddressMask.Mask(to)} via {smtp.Host}:{smtp.Port} failed: {exception.StatusCode} "
            + $"({(int)exception.StatusCode}). The relay's reply is left out because it can quote the full address; the relay's own log has it.");
        Exception? transportFault = exception.InnerException is SmtpException ? null : exception.InnerException;

        return new SmtpException(message, transportFault) { StatusCode = exception.StatusCode };
    }

    [LoggerMessage(EventId = 1205, EventName = "EmailSent", Level = LogLevel.Information,
        Message = "E-mail sent (Email:Provider=Smtp): to {MaskedRecipient}, subject \"{Subject}\", body of {BodyLength} characters")]
    private static partial void EmailSent(ILogger logger, string maskedRecipient, string subject, int bodyLength);
}
