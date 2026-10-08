#if (UseSerilog)
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using Serilog.Extensions.Logging;
using Shouldly;
using Trellis.SharedKernel.Decorators;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// With <c>--use-serilog</c>, Serilog is the host's logger factory, and it must still hand every event to
/// the OpenTelemetry logger provider (<c>writeToProviders</c>). Without that, logs silently vanish from the
/// Aspire dashboard and every OTLP backend while traces and metrics keep arriving. A processor added to the
/// OpenTelemetry logger provider sees exactly the records the OTLP exporter would send, so no exporter
/// package is needed to prove it.
/// </summary>
public sealed class SerilogExportTests(SerilogExportTests.Factory factory) : IClassFixture<SerilogExportTests.Factory>
{
    [Fact]
    public void Serilog_is_the_hosts_logger_factory()
        => factory.Services.GetRequiredService<ILoggerFactory>().ShouldBeOfType<SerilogLoggerFactory>();

    [Fact]
    public async Task Application_log_records_reach_the_OpenTelemetry_logger_provider()
    {
        // Signing in runs the login use case, whose logging decorator writes one outcome line at Information.
        await factory.CreateAdminClientAsync(TestContext.Current.CancellationToken);

        string useCaseCategory = typeof(LoggingDecorators).FullName!;
        factory.Exported.ShouldContain(
            record => record.Category.StartsWith(useCaseCategory, StringComparison.Ordinal)
                && record.Level == LogLevel.Information
                && record.Message.Contains("LoginCommand", StringComparison.Ordinal),
            $"exported: {string.Join(" | ", factory.Exported.Select(static r => r.Category).Distinct(StringComparer.Ordinal))}");
    }

    /// <summary>One exported record, copied out: OpenTelemetry pools <see cref="LogRecord"/> instances once the processor returns.</summary>
    public sealed record ExportedLog(string Category, LogLevel Level, string Message);

    /// <summary><see cref="ApiFactory"/> with a capturing processor on the OpenTelemetry logger provider.</summary>
    public sealed class Factory : ApiFactory
    {
        private readonly CapturingProcessor _processor = new();

        public IReadOnlyList<ExportedLog> Exported => _processor.Records;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
                services.ConfigureOpenTelemetryLoggerProvider(logging => logging.AddProcessor(_processor)));
        }
    }

    private sealed class CapturingProcessor : BaseProcessor<LogRecord>
    {
        private readonly ConcurrentQueue<ExportedLog> _records = new();

        public IReadOnlyList<ExportedLog> Records => [.. _records];

        public override void OnEnd(LogRecord data)
            => _records.Enqueue(new ExportedLog(data.CategoryName ?? string.Empty, data.LogLevel, data.FormattedMessage ?? data.Body ?? string.Empty));
    }
}
#endif
