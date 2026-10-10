using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Trellis.Application.Abstractions;
using Trellis.Infrastructure;
using Trellis.Infrastructure.Email;
using Xunit;

namespace Trellis.ApiTests;

/// <summary>
/// The e-mail port's adapters as the hosts register them: <c>Email:Provider</c> picks the sender, the
/// logging stand-in never writes a body or a full address, and the sending host (the API, through
/// <c>AddOutboundIntegrations</c>) checks the section at boot and says so when e-mail goes nowhere.
/// </summary>
public sealed class EmailSenderTests
{
    private const string Recipient = "jane.doe@example.com";
    private const string Subject = "Thanks for your payment";
    private const string Body = "Your password-reset link: https://example.com/reset?token=s3cr3t";

    [Fact]
    public async Task Send_WithTheLoggingProvider_LogsMaskedRecipientSubjectAndLengthOnly()
    {
        var logs = new CapturingLoggerProvider();
        await using ServiceProvider services = BuildServices(logs, provider: "Logging");

        await SendOneAsync(services);

        CapturedLog line = logs.All.ShouldHaveSingleItem();
        line.EventName.ShouldBe("EmailNotSent");
        line.Level.ShouldBe(LogLevel.Information);
        line.Message.ShouldContain("j***@example.com");
        line.Message.ShouldContain(Subject);
        line.Message.ShouldContain(Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        logs.All.ShouldNotContain(static entry => entry.Message.Contains(Recipient) || entry.Message.Contains("s3cr3t"));
    }

    [Fact]
    public async Task Send_WithProviderNone_DiscardsWithoutAnInformationLine()
    {
        var logs = new CapturingLoggerProvider();
        await using ServiceProvider services = BuildServices(logs, provider: "None");

        await SendOneAsync(services);

        CapturedLog line = logs.All.ShouldHaveSingleItem();
        line.EventName.ShouldBe("EmailDiscarded");
        line.Level.ShouldBe(LogLevel.Debug);
        line.Message.ShouldContain("j***@example.com");
        line.Message.ShouldNotContain(Recipient);
    }

    [Theory]
    [InlineData("Production", "Logging", "EmailLoggingStandInActive", LogLevel.Warning)]
    [InlineData("Staging", "Logging", "EmailLoggingStandInActive", LogLevel.Warning)]
    [InlineData("Production", "None", "EmailSwitchedOff", LogLevel.Information)]
    public async Task Boot_SendingHostWhoseEmailGoesNowhere_SaysSo(string environment, string provider, string eventName, LogLevel level)
    {
        var logs = new CapturingLoggerProvider();
        using IHost host = BuildHost(logs, environment, provider, sends: true);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        logs.All.ShouldContain(entry => entry.EventName == eventName && entry.Level == level);
    }

    [Fact]
    public async Task Boot_DevelopmentWithTheLoggingStandIn_StaysQuiet()
    {
        var logs = new CapturingLoggerProvider();
        using IHost host = BuildHost(logs, Environments.Development, "Logging", sends: true);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        logs.All.ShouldNotContain(static entry => entry.EventName == "EmailLoggingStandInActive" || entry.EventName == "EmailSwitchedOff");
    }

    [Theory]
    [InlineData("Smtp")]   // not a provider this build knows: the binder cannot convert it
    [InlineData("7")]      // converts to an undefined enum value: validation rejects it
    public async Task Boot_SendingHostWithAnUnknownProvider_Fails(string provider)
    {
        using IHost host = BuildHost(new CapturingLoggerProvider(), Environments.Production, provider, sends: true);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync(TestContext.Current.CancellationToken));

        IEnumerable<Exception> failures = failure is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [failure];
        failures.ShouldContain(static e => e.Message.Contains("Email:Provider") || e.Message.Contains("EmailOptions"));
    }

    [Fact]
    public async Task Boot_NonSendingHostWithAnUnknownProvider_StillStarts()
    {
        // The migration service's shape: AddInfrastructure registers the sender (event handlers must be
        // constructible), but nothing there sends, so its Email section is none of its business.
        using IHost host = BuildHost(new CapturingLoggerProvider(), Environments.Production, "7", sends: false);

        await host.StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested.ShouldBeTrue();
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Options_WithoutAnEmailSection_DefaultToTheLoggingStandIn()
    {
        using ServiceProvider services = BuildServices(new CapturingLoggerProvider(), provider: null);

        services.GetRequiredService<IOptions<EmailOptions>>().Value.Provider.ShouldBe(EmailProvider.Logging);
    }

    private static async Task SendOneAsync(ServiceProvider services)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IEmailSender>()
            .SendAsync(Recipient, Subject, Body, TestContext.Current.CancellationToken);
    }

    private static ServiceProvider BuildServices(CapturingLoggerProvider logs, string? provider)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (provider is not null)
        {
            settings["Email:Provider"] = provider;
        }

        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider();
    }

    /// <summary>The hosts' registration order on a host that reads only the settings given here.</summary>
    private static IHost BuildHost(CapturingLoggerProvider logs, string environment, string provider, bool sends)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ApplicationName = typeof(EmailSenderTests).Assembly.GetName().Name,
            EnvironmentName = environment,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["Email:Provider"] = provider,
        });
        builder.Logging.AddProvider(logs);

        builder.Services.AddInfrastructure(builder.Configuration);
        if (sends)
        {
            builder.Services.AddOutboundIntegrations(builder.Configuration);
        }

        return builder.Build();
    }

    private sealed record CapturedLog(LogLevel Level, string? EventName, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedLog> _entries = new();

        public IReadOnlyList<CapturedLog> All => [.. _entries];

        public ILogger CreateLogger(string categoryName)
            => categoryName.StartsWith("Trellis.Infrastructure.Email", StringComparison.Ordinal)
                ? new CapturingLogger(_entries)
                : Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;

        public void Dispose()
        {
            // Nothing to release; the entries outlive the host on purpose.
        }

        private sealed class CapturingLogger(ConcurrentQueue<CapturedLog> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new CapturedLog(logLevel, eventId.Name, formatter(state, exception)));
        }
    }
}
