using System.Globalization;
using System.Net.Mail;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;
using MimeKit.Text;
using Trellis.Application.Abstractions;
using SmtpClient = MailKit.Net.Smtp.SmtpClient;

namespace Trellis.Infrastructure.Email;

/// <summary>
/// <c>Email:Provider=Smtp</c>: submits each message to the relay in <see cref="SmtpOptions"/> with MailKit's
/// <see cref="SmtpClient"/>, upgraded with STARTTLS unless a local catcher was configured.
/// <para>
/// One client per message: a MailKit client is one connection that runs one command at a time, so a shared one
/// would fail overlapping handlers; disposing it closes the connection. The message is plain text, from
/// <see cref="SmtpOptions.FromAddress"/> to the one recipient. The connection is upgraded with STARTTLS before
/// anything else is sent, and fails rather than falls back to plaintext when the relay does not offer it
/// (<see cref="SecureSocketOptions.StartTls"/>, a <see cref="NotSupportedException"/> before <c>MAIL FROM</c>);
/// <see cref="SmtpOptions.UseStartTls"/> false sends in clear (<see cref="SecureSocketOptions.None"/>), which the
/// validator allows only without credentials. The client authenticates only when credentials are configured.
/// </para>
/// <para>
/// A failure is thrown, not returned. The port returns <see cref="Task"/>, so a throw is its only failure
/// signal, and a relay outage is a fault rather than an expected outcome (ADR-0004): the outbox processor
/// that runs the event handler records it and retries with back-off. The relay's reply is dropped from
/// what is thrown, because a rejected recipient's reply usually quotes the full address (and MailKit's
/// <see cref="SmtpCommandException.Mailbox"/> carries it) while the outbox writes the message into its row and
/// the log; the error and status codes and the masked recipient stay. Delivery is at least once — a crash
/// between the relay's acceptance and the outbox's checkpoint sends again; a relay that accepted the message
/// and then fails the <c>QUIT</c> does not, since MailKit's disconnect ignores that failure.
/// </para>
/// <para>
/// Cancellation is the caller's: the token reaches every call to the relay, and the outbox gives each message's
/// handlers <c>Outbox:DispatchTimeout</c> (30 s).
/// </para>
/// </summary>
internal sealed partial class SmtpEmailSender(IOptions<SmtpOptions> options, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    private const string ReplyLeftOut = "The relay's reply is left out because it can quote the full address; the relay's own log has it.";

    public async Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        SmtpOptions smtp = options.Value;

        using var message = new MimeMessage();
        message.From.Add(Mailbox(smtp.FromAddress));
        message.To.Add(Mailbox(to));
        message.Subject = subject;
        message.Body = new TextPart(TextFormat.Plain) { Text = body };

        using var client = new SmtpClient();
        try
        {
            await client.ConnectAsync(smtp.Host, smtp.Port, smtp.UseStartTls ? SecureSocketOptions.StartTls : SecureSocketOptions.None, cancellationToken);
            // SmtpOptionsValidator holds the two together: both set, or neither.
            if (smtp is { UserName: { Length: > 0 } userName, Password: { Length: > 0 } password })
            {
                await client.AuthenticateAsync(userName, password, cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(quit: true, cancellationToken);
        }
        catch (SmtpCommandException exception)
        {
            throw Redacted(exception, to, smtp);
        }
        catch (SmtpProtocolException exception)
        {
            string reason = Failed(to, smtp, "the relay broke off the SMTP conversation.");
            throw TransportFault(exception) is { } fault ? new SmtpProtocolException(reason, fault) : new SmtpProtocolException(reason);
        }
        catch (AuthenticationException exception)
        {
            string reason = Failed(to, smtp, "the relay refused Email:Smtp:UserName and Password.");
            throw TransportFault(exception) is { } fault ? new AuthenticationException(reason, fault) : new AuthenticationException(reason);
        }
        catch (ServiceNotAuthenticatedException exception)
        {
            string reason = Failed(to, smtp, "the relay wants authentication: set Email:Smtp:UserName and Password.");
            throw TransportFault(exception) is { } fault ? new ServiceNotAuthenticatedException(reason, fault) : new ServiceNotAuthenticatedException(reason);
        }

        // Masking allocates; skip it when nobody listens.
        if (logger.IsEnabled(LogLevel.Information))
        {
            string maskedRecipient = EmailAddressMask.Mask(to);
            EmailSent(logger, maskedRecipient, subject, body.Length);
        }
    }

    /// <summary>
    /// An address read by the BCL parser <see cref="SmtpOptionsValidator"/> checks <see cref="SmtpOptions.FromAddress"/>
    /// with, so what boot accepted is what the message carries, display name included.
    /// </summary>
    private static MailboxAddress Mailbox(string address)
    {
        var parsed = new MailAddress(address);
        return new MailboxAddress(parsed.DisplayName, parsed.Address);
    }

    /// <summary>
    /// The same failure without the relay's reply or the rejected mailbox: error and status code, masked
    /// recipient and relay endpoint.
    /// </summary>
    private static SmtpCommandException Redacted(SmtpCommandException exception, string to, SmtpOptions smtp)
    {
        string reason = Failed(to, smtp, string.Create(CultureInfo.InvariantCulture,
            $"{exception.ErrorCode}, {exception.StatusCode} ({(int)exception.StatusCode})."));
        return TransportFault(exception) is { } fault
            ? new SmtpCommandException(exception.ErrorCode, exception.StatusCode, reason, fault)
            : new SmtpCommandException(exception.ErrorCode, exception.StatusCode, reason);
    }

    private static string Failed(string to, SmtpOptions smtp, string what)
        => string.Create(CultureInfo.InvariantCulture,
            $"SMTP submission to {EmailAddressMask.Mask(to)} via {smtp.Host}:{smtp.Port} failed: {what} {ReplyLeftOut}");

    /// <summary>
    /// The fault underneath (a dropped connection), which names no recipient. A nested exception that carries a
    /// relay reply is dropped for the reason its parent is.
    /// </summary>
    private static Exception? TransportFault(Exception exception)
        => exception.InnerException is SmtpCommandException or SmtpProtocolException or AuthenticationException or ServiceNotAuthenticatedException
            ? null
            : exception.InnerException;

    [LoggerMessage(EventId = 1205, EventName = "EmailSent", Level = LogLevel.Information,
        Message = "E-mail sent (Email:Provider=Smtp): to {MaskedRecipient}, subject \"{Subject}\", body of {BodyLength} characters")]
    private static partial void EmailSent(ILogger logger, string maskedRecipient, string subject, int bodyLength);
}
