using Microsoft.Extensions.Logging;
using Trellis.Application.Abstractions;

namespace Trellis.Infrastructure.Email;

/// <summary>
/// Development stand-in for outbound mail (<c>Email:Provider=Logging</c>): one log line per message,
/// nothing sent. The line carries the masked recipient, the subject and the body's length — never the
/// body or the full address, because a receipt or a password-reset link in a log is a data leak with a
/// retention period. Replace it with a real provider adapter before production; the API warns at boot
/// while it is active outside Development.
/// </summary>
internal sealed partial class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        // Masking allocates; skip it when nobody listens.
        if (logger.IsEnabled(LogLevel.Information))
        {
            string maskedRecipient = EmailAddressMask.Mask(to);
            EmailNotSent(logger, maskedRecipient, subject, body.Length);
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 1201, EventName = "EmailNotSent", Level = LogLevel.Information,
        Message = "E-mail not sent (Email:Provider=Logging): to {MaskedRecipient}, subject \"{Subject}\", body of {BodyLength} characters")]
    private static partial void EmailNotSent(ILogger logger, string maskedRecipient, string subject, int bodyLength);
}
