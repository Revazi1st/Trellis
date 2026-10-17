using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Trellis.Application.UnitTests;

/// <summary>
/// One formatted log call, as a test reads it back. <see cref="SpanId"/> is the span current when the
/// line was written — what an OpenTelemetry log record links to — or null outside any span.
/// </summary>
internal sealed record LogEntry(Type Category, LogLevel Level, EventId EventId, string Message, Exception? Exception, ActivitySpanId? SpanId);

/// <summary>
/// Collects every line written through <see cref="CapturingLogger{T}"/>. A hand-rolled double on
/// purpose: <c>Microsoft.Extensions.Diagnostics.Testing</c>'s FakeLogger is not in the package set,
/// and capturing (category, level, event id, message, exception, current span) is all the outcome tests need.
/// </summary>
internal sealed class LogCapture
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    /// <summary>
    /// The lines the pipeline's logging stage wrote — one per use-case invocation — matched on the use-case
    /// event ids (1001–1099, ADR-0028), not on the category: each use case logs under its own message type.
    /// </summary>
    public IReadOnlyList<LogEntry> OutcomeLines
        => [.. _entries.Where(static e => e.EventId.Id is >= 1001 and <= 1099)];

    public void Add(LogEntry entry) => _entries.Enqueue(entry);
}

/// <summary>Registered as the open generic <c>ILogger&lt;&gt;</c>; every instance writes into the shared <see cref="LogCapture"/>.</summary>
internal sealed class CapturingLogger<T>(LogCapture capture) : ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => capture.Add(new LogEntry(typeof(T), logLevel, eventId, formatter(state, exception), exception, Activity.Current?.SpanId));
}
