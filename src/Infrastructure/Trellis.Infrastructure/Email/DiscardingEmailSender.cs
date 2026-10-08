using Microsoft.Extensions.Logging;
using Trellis.Application.Abstractions;

namespace Trellis.Infrastructure.Email;

/// <summary>
/// <c>Email:Provider=None</c>: outbound e-mail switched off on purpose. Every message is dropped; a Debug
/// line (masked recipient and subject only) lets someone tracing a flow see that it would have gone out.
/// </summary>
internal sealed partial class DiscardingEmailSender(ILogger<DiscardingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            string maskedRecipient = EmailAddressMask.Mask(to);
            EmailDiscarded(logger, maskedRecipient, subject);
        }

        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 1202, EventName = "EmailDiscarded", Level = LogLevel.Debug,
        Message = "E-mail discarded (Email:Provider=None): to {MaskedRecipient}, subject \"{Subject}\"")]
    private static partial void EmailDiscarded(ILogger logger, string maskedRecipient, string subject);
}
