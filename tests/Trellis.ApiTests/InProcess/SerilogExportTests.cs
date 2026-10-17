#if (UseSerilog)
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Logs;
using OpenTelemetry.Trace;
using Serilog.Extensions.Logging;
using Shouldly;
using Trellis.Application.Features.Auth.Login;
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
        // Signing in runs the login use case, whose logging decorator writes one outcome line at Information,
        // under the use case's own category: what a Serilog:MinimumLevel:Override for a feature's namespace keys on.
        await factory.CreateAdminClientAsync(TestContext.Current.CancellationToken);

        factory.Exported.ShouldContain(
            record => IsUseCaseLine(record)
                && record.Category == typeof(LoginCommand).FullName
                && record.Level == LogLevel.Information
                && record.Message.Contains("LoginCommand", StringComparison.Ordinal),
            $"exported: {string.Join(" | ", factory.Exported.Select(static r => r.Category).Distinct(StringComparer.Ordinal))}");
    }

    [Fact]
    public async Task The_outcome_record_keeps_its_event_id_and_links_to_the_use_case_span()
    {
        await factory.CreateAdminClientAsync(TestContext.Current.CancellationToken);

        // Tracing wraps Logging (ADR-0003), and the bridge hands the event on while the use-case span is
        // still current: the exported record names that span, not the HTTP request's, and keeps its id.
        HashSet<ActivitySpanId> loginSpans = [.. factory.Spans
            .Where(static span => span.Name == "LoginCommand")
            .Select(static span => span.SpanId)];
        ExportedLog[] outcomes = [.. factory.Exported
            .Where(static record => IsUseCaseLine(record) && record.Message.Contains("LoginCommand", StringComparison.Ordinal))];

        loginSpans.ShouldNotBeEmpty();
        outcomes.ShouldNotBeEmpty();
        outcomes.ShouldAllBe(record => record.EventId == 1001 && loginSpans.Contains(record.SpanId));
    }

    /// <summary>A line of the use-case logging stage, by its event id (1001–1099, ADR-0028): its category is the use case's own type.</summary>
    private static bool IsUseCaseLine(ExportedLog record) => record.EventId is >= 1001 and <= 1099;

    /// <summary>One exported record, copied out: OpenTelemetry pools <see cref="LogRecord"/> instances once the processor returns.</summary>
    public sealed record ExportedLog(string Category, LogLevel Level, int EventId, string Message, ActivitySpanId SpanId);

    /// <summary>One ended use-case span: its name and id.</summary>
    public sealed record EndedSpan(string Name, ActivitySpanId SpanId);

    /// <summary>
    /// <see cref="ApiFactory"/> with a capturing processor on the OpenTelemetry logger provider, and one on the
    /// tracer provider for the use-case spans.
    /// </summary>
    public sealed class Factory : ApiFactory
    {
        private readonly CapturingProcessor _processor = new();
        private readonly UseCaseSpanProcessor _spans = new();

        public IReadOnlyList<ExportedLog> Exported => _processor.Records;

        public IReadOnlyList<EndedSpan> Spans => _spans.Spans;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services =>
            {
                services.ConfigureOpenTelemetryLoggerProvider(logging => logging.AddProcessor(_processor));
                services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddProcessor(_spans));
            });
        }
    }

    private sealed class CapturingProcessor : BaseProcessor<LogRecord>
    {
        private readonly ConcurrentQueue<ExportedLog> _records = new();

        public IReadOnlyList<ExportedLog> Records => [.. _records];

        public override void OnEnd(LogRecord data)
            => _records.Enqueue(new ExportedLog(
                data.CategoryName ?? string.Empty,
                data.LogLevel,
                data.EventId.Id,
                data.FormattedMessage ?? data.Body ?? string.Empty,
                data.SpanId));
    }

    private sealed class UseCaseSpanProcessor : BaseProcessor<Activity>
    {
        private readonly ConcurrentQueue<EndedSpan> _spans = new();

        public IReadOnlyList<EndedSpan> Spans => [.. _spans];

        public override void OnEnd(Activity data)
        {
            if (data.Source.Name == TracingDecorators.ActivitySourceName)
            {
                _spans.Enqueue(new EndedSpan(data.OperationName, data.SpanId));
            }
        }
    }
}
#endif
