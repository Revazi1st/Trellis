using Microsoft.Extensions.Logging;
using XTemplate.Application.Abstractions;

namespace XTemplate.Infrastructure.Email;

/// <summary>Development stand-in for outbound mail: writes structured log entries instead of sending. Swap for a real provider adapter in production.</summary>
internal sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : IEmailSender
{
    public Task SendAsync(string to, string subject, string body, CancellationToken cancellationToken)
    {
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Email (dev stub) to {To} — {Subject}: {Body}", to, subject, body);
        }

        return Task.CompletedTask;
    }
}
