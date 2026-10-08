using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Trellis.Infrastructure.Email;

/// <summary>
/// Says at boot, once, when the sending host's outbound e-mail goes nowhere: a Warning while the logging
/// stand-in is active outside Development (a deployment that forgot to configure a provider), an
/// Information line when e-mail is switched off on purpose. Silence means a real provider, or
/// Development's expected stand-in.
/// </summary>
internal sealed partial class EmailProviderStartupNotice(
    IOptions<EmailOptions> options,
    IHostEnvironment environment,
    ILogger<EmailProviderStartupNotice> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        EmailProvider provider = options.Value.Provider;

        if (provider == EmailProvider.Logging && !environment.IsDevelopment())
        {
            LoggingStandInActive(logger, environment.EnvironmentName);
        }
        else if (provider == EmailProvider.None)
        {
            EmailSwitchedOff(logger);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    [LoggerMessage(EventId = 1203, EventName = "EmailLoggingStandInActive", Level = LogLevel.Warning,
        Message = "Outbound e-mail is NOT being sent: Email:Provider is Logging in the {EnvironmentName} environment, so every "
            + "message is only logged (masked recipient, no body) and then dropped. Configure a real provider, or set "
            + "Email:Provider=None to discard e-mail on purpose.")]
    private static partial void LoggingStandInActive(ILogger logger, string environmentName);

    [LoggerMessage(EventId = 1204, EventName = "EmailSwitchedOff", Level = LogLevel.Information,
        Message = "Outbound e-mail is switched off (Email:Provider=None): every message is discarded.")]
    private static partial void EmailSwitchedOff(ILogger logger);
}
