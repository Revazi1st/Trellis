using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Trellis.UiTests;

/// <summary>
/// Keeps what the application <see cref="AppHostFixture"/> started printed. Aspire's testing builder forwards
/// every console line of every resource to the AppHost's own loggers, under the category
/// <c>{AppHost}.Resources.{resource}</c>; this provider writes each resource's lines to
/// <c>{resource}.log</c> in <see cref="Folder"/> as they arrive, the AppHost's own lines to
/// <c>apphost.log</c>, and remembers the last lines of each for the message of a failed start. The files are
/// what explains a start that failed: CI prints them and keeps them as an artifact.
/// </summary>
internal sealed class AppLogs(string folder) : ILoggerProvider
{
    /// <summary>The file of everything that is not a resource's console: Aspire's and the AppHost's own lines.</summary>
    public const string AppHost = "apphost";

    private const string ResourceInfix = ".Resources.";
    private const int TailLength = 15;

    private readonly ConcurrentDictionary<string, Sink> _sinks = new(StringComparer.Ordinal);

    public string Folder { get; } = folder;

    public ILogger CreateLogger(string categoryName)
        => _sinks.GetOrAdd(SinkNameOf(categoryName), name => new Sink(Path.Combine(Folder, name + ".log")));

    /// <summary>The last lines <paramref name="sink"/> received, oldest first; none when it received nothing.</summary>
    public IReadOnlyList<string> Tail(string sink)
        => _sinks.TryGetValue(sink, out Sink? found) ? found.Tail() : [];

    public void Dispose()
    {
        foreach (Sink sink in _sinks.Values)
        {
            sink.Dispose();
        }
    }

    /// <summary><c>Acme.AppHost.Resources.api</c> is <c>api</c>; any other category is the AppHost's own.</summary>
    internal static string SinkNameOf(string category)
    {
        int at = category.LastIndexOf(ResourceInfix, StringComparison.Ordinal);
        return at < 0 || at + ResourceInfix.Length == category.Length ? AppHost : category[(at + ResourceInfix.Length)..];
    }

    private sealed class Sink(string path) : ILogger, IDisposable
    {
        private readonly Lock _gate = new();
        private readonly Queue<string> _tail = new();
        private StreamWriter? _file;
        private bool _closed;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string line = string.Create(CultureInfo.InvariantCulture, $"{logLevel,-11} {formatter(state, exception)}");
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            lock (_gate)
            {
                if (_closed)
                {
                    return;
                }

                if (_file is null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    // Flushed line by line: a job that is cancelled mid-start still leaves what was printed.
                    _file = new StreamWriter(path, append: false) { AutoFlush = true };
                }

                _file.WriteLine(line);
                _tail.Enqueue(line);
                if (_tail.Count > TailLength)
                {
                    _tail.Dequeue();
                }
            }
        }

        public string[] Tail()
        {
            lock (_gate)
            {
                return [.. _tail];
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _closed = true;
                _file?.Dispose();
                _file = null;
            }
        }
    }
}
