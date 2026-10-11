using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
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
/// The e-mail port's adapters as the hosts register them: <c>Email:Provider</c> picks the sender, no
/// adapter writes a body or a full address, the SMTP adapter really talks SMTP (to a fake relay on a
/// loopback socket), and the sending host (the API, through <c>AddOutboundIntegrations</c>) checks the
/// section at boot — the relay only while SMTP is selected — and says so when e-mail goes nowhere or in clear.
/// </summary>
public sealed class EmailSenderTests
{
    private const string Recipient = "jane.doe@example.com";
    private const string Subject = "Thanks for your payment";
    private const string Body = "Your password-reset link: https://example.com/reset?token=s3cr3t";
    private const string RelayPassword = "relay-password-s3cr3t";

    [Fact]
    public async Task Send_WithTheLoggingProvider_LogsMaskedRecipientSubjectAndLengthOnly()
    {
        var logs = new CapturingLoggerProvider();
        await using ServiceProvider services = BuildServices(logs, Settings("Logging"));

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
        await using ServiceProvider services = BuildServices(logs, Settings("None"));

        await SendOneAsync(services);

        CapturedLog line = logs.All.ShouldHaveSingleItem();
        line.EventName.ShouldBe("EmailDiscarded");
        line.Level.ShouldBe(LogLevel.Debug);
        line.Message.ShouldContain("j***@example.com");
        line.Message.ShouldNotContain(Recipient);
    }

    [Theory]
    [InlineData("Smtp", "SmtpEmailSender")]
    [InlineData("Logging", "LoggingEmailSender")]
    [InlineData("None", "DiscardingEmailSender")]
    public async Task Resolve_EachProvider_GetsItsAdapter(string provider, string adapter)
    {
        await using ServiceProvider services = BuildServices(new CapturingLoggerProvider(), Relay($"Email:Provider={provider}"));
        await using AsyncServiceScope scope = services.CreateAsyncScope();

        scope.ServiceProvider.GetRequiredService<IEmailSender>().GetType().Name.ShouldBe(adapter);
    }

    [Fact]
    public async Task Send_WithTheSmtpProvider_SubmitsTheMessageAndLogsItMasked()
    {
        using var relay = new TcpListener(IPAddress.Loopback, 0);
        relay.Start();
        Task<string> conversation = FakeRelayAsync(relay, recipientReply: "250 ok", TestContext.Current.CancellationToken);
        var logs = new CapturingLoggerProvider();
        await using ServiceProvider services = BuildServices(logs, LocalRelay(relay));

        await SendOneAsync(services);

        string wire = await conversation;
        wire.ShouldContain("MAIL FROM:<billing@example.com>");
        wire.ShouldContain($"RCPT TO:<{Recipient}>");
        wire.ShouldContain($"Subject: {Subject}");
        wire.ShouldContain("Your password-reset link");
        CapturedLog line = logs.All.ShouldHaveSingleItem();
        line.EventName.ShouldBe("EmailSent");
        line.Message.ShouldContain("j***@example.com");
        line.Message.ShouldContain(Body.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
        logs.All.ShouldNotContain(static entry => entry.Message.Contains(Recipient) || entry.Message.Contains("s3cr3t"));
    }

    [Fact]
    public async Task Send_WhenTheRelayRejectsTheRecipient_ThrowsWithoutTheAddress()
    {
        // Relays quote the rejected address in their reply; the outbox writes what is thrown into its row and its log.
        using var relay = new TcpListener(IPAddress.Loopback, 0);
        relay.Start();
        Task<string> conversation = FakeRelayAsync(
            relay, recipientReply: $"550 5.1.1 <{Recipient}>: Recipient address rejected", TestContext.Current.CancellationToken);
        await using ServiceProvider services = BuildServices(new CapturingLoggerProvider(), LocalRelay(relay));

        SmtpException failure = await Should.ThrowAsync<SmtpException>(() => SendOneAsync(services));
        await conversation;

        failure.StatusCode.ShouldBe(SmtpStatusCode.MailboxUnavailable);
        failure.Message.ShouldContain("j***@example.com");
        failure.ToString().ShouldNotContain(Recipient);
    }

    [Fact]
    public async Task Send_ToARelayThatNeverAnswers_StopsWhenTheCallerCancels()
    {
        // The outbox's dispatch budget is the only clock on a send: the listener accepts the connection
        // (the backlog does) and never greets, so only the caller's token can end the wait.
        using var relay = new TcpListener(IPAddress.Loopback, 0);
        relay.Start();
        await using ServiceProvider services = BuildServices(new CapturingLoggerProvider(), LocalRelay(relay));
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        caller.CancelAfter(TimeSpan.FromMilliseconds(200));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            scope.ServiceProvider.GetRequiredService<IEmailSender>().SendAsync(Recipient, Subject, Body, caller.Token));

        elapsed.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData("Email:Smtp:Host=", "Email:Smtp:Host is required")]
    [InlineData("Email:Smtp:FromAddress=", "Email:Smtp:FromAddress is required")]
    [InlineData("Email:Smtp:FromAddress=billing at example.com", "Email:Smtp:FromAddress is not an e-mail address")]
    [InlineData("Email:Smtp:Port=0", "Email:Smtp:Port must be between")]
    [InlineData("Email:Smtp:Port=465", "implicit TLS")]
    [InlineData("Email:Smtp:UserName=relay-user", "go together")]
    [InlineData("Email:Smtp:Password=" + RelayPassword, "go together")]
    [InlineData("Email:Smtp:UserName=relay-user;Email:Smtp:Password=" + RelayPassword + ";Email:Smtp:UseStartTls=false", "credentials in clear")]
    public async Task Boot_SendingHostWithABrokenRelay_FailsNamingTheSetting(string overrides, string expected)
    {
        using IHost host = BuildHost(new CapturingLoggerProvider(), Environments.Production, Relay(overrides.Split(';')), sends: true);

        IReadOnlyList<Exception> failures = await BootFailuresAsync(host);

        failures.ShouldContain(e => e.Message.Contains(expected));
        failures.ShouldNotContain(static e => e.ToString().Contains(RelayPassword));
    }

    [Fact]
    public async Task Boot_SendingHostWithABrokenRelayThatIsNotSelected_StillStarts()
    {
        // The relay is read only while SMTP is the provider: a half-written section does not stop a host that discards.
        using IHost host = BuildHost(
            new CapturingLoggerProvider(), Environments.Production, Settings("None", "Email:Smtp:Port=465", "Email:Smtp:UserName=relay-user"), sends: true);

        await host.StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested.ShouldBeTrue();
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("true", "Production", false)]
    [InlineData("false", "Development", false)]
    [InlineData("false", "Production", true)]
    public async Task Boot_SendingHostWithARelay_WarnsOnlyWhenPlaintextReachesARealEnvironment(string useStartTls, string environment, bool warns)
    {
        var logs = new CapturingLoggerProvider();
        using IHost host = BuildHost(logs, environment, Relay($"Email:Smtp:UseStartTls={useStartTls}"), sends: true);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        logs.All.Any(static entry => entry.EventName == "EmailSmtpInClear" && entry.Level == LogLevel.Warning).ShouldBe(warns);
        logs.All.ShouldNotContain(static entry => entry.EventName == "EmailLoggingStandInActive" || entry.EventName == "EmailSwitchedOff");
    }

    [Theory]
    [InlineData("Production", "Logging", "EmailLoggingStandInActive", LogLevel.Warning)]
    [InlineData("Staging", "Logging", "EmailLoggingStandInActive", LogLevel.Warning)]
    [InlineData("Production", "None", "EmailSwitchedOff", LogLevel.Information)]
    public async Task Boot_SendingHostWhoseEmailGoesNowhere_SaysSo(string environment, string provider, string eventName, LogLevel level)
    {
        var logs = new CapturingLoggerProvider();
        using IHost host = BuildHost(logs, environment, Settings(provider), sends: true);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        logs.All.ShouldContain(entry => entry.EventName == eventName && entry.Level == level);
    }

    [Fact]
    public async Task Boot_DevelopmentWithTheLoggingStandIn_StaysQuiet()
    {
        var logs = new CapturingLoggerProvider();
        using IHost host = BuildHost(logs, Environments.Development, Settings("Logging"), sends: true);

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);

        logs.All.ShouldNotContain(static entry => entry.EventName == "EmailLoggingStandInActive" || entry.EventName == "EmailSwitchedOff");
    }

    [Theory]
    [InlineData("SendGrid")]   // not a provider this build knows: the binder cannot convert it
    [InlineData("7")]          // converts to an undefined enum value: validation rejects it
    public async Task Boot_SendingHostWithAnUnknownProvider_Fails(string provider)
    {
        using IHost host = BuildHost(new CapturingLoggerProvider(), Environments.Production, Settings(provider), sends: true);

        IReadOnlyList<Exception> failures = await BootFailuresAsync(host);

        failures.ShouldContain(static e => e.Message.Contains("Email:Provider") || e.Message.Contains("EmailOptions"));
    }

    [Fact]
    public async Task Boot_NonSendingHostWithAnUnknownProvider_StillStarts()
    {
        // The migration service's shape: AddInfrastructure registers the sender (event handlers must be
        // constructible), but nothing there sends, so its Email section is none of its business.
        using IHost host = BuildHost(new CapturingLoggerProvider(), Environments.Production, Settings("7"), sends: false);

        await host.StartAsync(TestContext.Current.CancellationToken);

        host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStarted.IsCancellationRequested.ShouldBeTrue();
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Options_WithoutAnEmailSection_DefaultToTheLoggingStandIn()
    {
        using ServiceProvider services = BuildServices(new CapturingLoggerProvider(), Settings(provider: null));

        services.GetRequiredService<IOptions<EmailOptions>>().Value.Provider.ShouldBe(EmailProvider.Logging);
    }

    private static async Task SendOneAsync(ServiceProvider services)
    {
        await using AsyncServiceScope scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IEmailSender>()
            .SendAsync(Recipient, Subject, Body, TestContext.Current.CancellationToken);
    }

    /// <summary>The settings a test states, and nothing else: <c>Email:Provider</c> when given, then each <c>key=value</c>.</summary>
    private static Dictionary<string, string?> Settings(string? provider, params string[] overrides)
    {
        var settings = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (provider is not null)
        {
            settings["Email:Provider"] = provider;
        }

        foreach (string pair in overrides)
        {
            int equals = pair.IndexOf('=', StringComparison.Ordinal);
            settings[pair[..equals]] = pair[(equals + 1)..];
        }

        return settings;
    }

    /// <summary>SMTP selected with a complete relay section; <paramref name="overrides"/> break or change one setting of it.</summary>
    private static Dictionary<string, string?> Relay(params string[] overrides)
        => Settings("Smtp", ["Email:Smtp:Host=smtp.example.com", "Email:Smtp:Port=587", "Email:Smtp:FromAddress=billing@example.com", .. overrides]);

    /// <summary>The relay pointed at the fake on <paramref name="listener"/>, in clear: the fake speaks no TLS.</summary>
    private static Dictionary<string, string?> LocalRelay(TcpListener listener)
        => Relay("Email:Smtp:Host=127.0.0.1", $"Email:Smtp:Port={((IPEndPoint)listener.LocalEndpoint).Port}", "Email:Smtp:UseStartTls=false");

    private static async Task<IReadOnlyList<Exception>> BootFailuresAsync(IHost host)
    {
        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync(TestContext.Current.CancellationToken));
        return failure is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [failure];
    }

    /// <summary>
    /// Just enough of a relay for one plaintext submission: greets, answers RCPT with
    /// <paramref name="recipientReply"/> and every other command 250 (221 for QUIT), swallows the DATA
    /// block, and returns everything the client sent once it hangs up — with QUIT after a delivery, with a
    /// connection reset after a failure.
    /// </summary>
    private static async Task<string> FakeRelayAsync(TcpListener listener, string recipientReply, CancellationToken cancellationToken)
    {
        using TcpClient connection = await listener.AcceptTcpClientAsync(cancellationToken);
        await using NetworkStream stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII);
        await using var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
        var conversation = new StringBuilder();

        async Task<string?> ReadLineAsync()
        {
            try
            {
                return await reader.ReadLineAsync(cancellationToken);
            }
            catch (IOException)
            {
                return null;
            }
        }

        await writer.WriteLineAsync("220 relay.test ESMTP");
        while (await ReadLineAsync() is { } line)
        {
            conversation.AppendLine(line);
            string verb = line.Split(' ', 2)[0].ToUpperInvariant();
            if (verb == "DATA")
            {
                await writer.WriteLineAsync("354 end with <CRLF>.<CRLF>");
                while (await ReadLineAsync() is { } data && data != ".")
                {
                    conversation.AppendLine(data);
                }
            }

            await writer.WriteLineAsync(verb switch
            {
                "RCPT" => recipientReply,
                "QUIT" => "221 bye",
                _ => "250 ok",
            });
        }

        return conversation.ToString();
    }

    private static ServiceProvider BuildServices(CapturingLoggerProvider logs, Dictionary<string, string?> settings)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddInfrastructure(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        return services.BuildServiceProvider();
    }

    /// <summary>The hosts' registration order on a host that reads only the settings given here.</summary>
    private static IHost BuildHost(CapturingLoggerProvider logs, string environment, Dictionary<string, string?> settings, bool sends)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
            ApplicationName = typeof(EmailSenderTests).Assembly.GetName().Name,
            EnvironmentName = environment,
        });
        builder.Configuration.AddInMemoryCollection(settings);
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
