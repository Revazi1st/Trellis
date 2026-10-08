using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Trellis.SharedKernel.Decorators;

namespace Trellis.Application.UnitTests;

/// <summary>One formatted log call, as a test reads it back.</summary>
internal sealed record LogEntry(Type Category, LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// Collects every line written through <see cref="CapturingLogger{T}"/>. A hand-rolled double on
/// purpose: <c>Microsoft.Extensions.Diagnostics.Testing</c>'s FakeLogger is not in the package set,
/// and capturing (category, level, message, exception) is all the level-table tests need.
/// </summary>
internal sealed class LogCapture
{
    private readonly ConcurrentQueue<LogEntry> _entries = new();

    public IReadOnlyList<LogEntry> Entries => [.. _entries];

    /// <summary>The lines the pipeline's outermost stage wrote — one per use-case invocation.</summary>
    public IReadOnlyList<LogEntry> OutcomeLines
        => [.. _entries.Where(static e => e.Category.IsGenericType && e.Category.DeclaringType == typeof(LoggingDecorators))];

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
        => capture.Add(new LogEntry(typeof(T), logLevel, formatter(state, exception), exception));
}
