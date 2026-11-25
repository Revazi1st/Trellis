using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Trellis.UiTests;

/// <summary>
/// Keeps what the application <see cref="AppHostFixture"/> started printed. Aspire's testing builder forwards
/// every console line of every resource to the AppHost's own loggers, under the category
/// <c>{AppHost}.Resources.{resource}</c>; this provider writes each resource's lines to
/// <c>{resource}.log</c> in <see cref="Folder"/> as they arrive, the AppHost's own lines to
/// <c>apphost.log</c>, and remembers two things of each for the message of a failed start: its first lines
/// logged at Error or above, and its last lines, a line's exception by its first line only (the file holds
/// the whole of it). The first error lines are there because the last ones are often the bottom of a stack
/// trace, while what went wrong is the exception's message above it (a resource's standard error arrives at
/// Error). The files are what explains a start that failed: CI prints them and keeps them as an artifact.
/// </summary>
internal sealed class AppLogs(string folder) : ILoggerProvider
{
    /// <summary>The file of everything that is not a resource's console: Aspire's and the AppHost's own lines.</summary>
    public const string AppHost = "apphost";

    private const string ResourceInfix = ".Resources.";
    private const int TailLength = 15;
    private const int FirstErrorsLength = 5;

    /// <summary>Puts an exception's first line under its log line's text, past the level's column.</summary>
    private const string Indent = "            ";

    private readonly ConcurrentDictionary<string, Sink> _sinks = new(StringComparer.Ordinal);

    public string Folder { get; } = folder;

    public ILogger CreateLogger(string categoryName)
        => _sinks.GetOrAdd(SinkNameOf(categoryName), name => new Sink(Path.Combine(Folder, name + ".log")));

    /// <summary>
    /// What <paramref name="sink"/> remembers, in one read: the first lines it received at Error or above,
    /// without those that are among its last lines (the tail holds them), and its last lines, each oldest
    /// first; both empty when it received nothing. One read, because the resources may still be printing: a
    /// line that leaves the tail between two reads would be in neither.
    /// </summary>
    public (IReadOnlyList<string> FirstErrors, IReadOnlyList<string> Tail) Read(string sink)
        => _sinks.TryGetValue(sink, out Sink? found) ? found.Read() : ([], []);

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
        private readonly List<(long Number, string Line)> _firstErrors = [];
        private StreamWriter? _file;
        private long _received;
        private bool _closed;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            string line = string.Create(CultureInfo.InvariantCulture, $"{logLevel,-11} {formatter(state, exception)}");
            // The file takes an exception whole. The message of a failed start takes its first line, the type
            // and the message: the AppHost logs every failed health check with its exception, and their stack
            // traces would fill the AppHost's last lines as its HTTP client's requests did.
            string remembered = line;
            if (exception is not null)
            {
                string whole = exception.ToString();
                line += Environment.NewLine + whole;
                int end = whole.IndexOfAny(['\r', '\n']);
                remembered += Environment.NewLine + Indent + (end < 0 ? whole : whole[..end]);
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
                _received++;
                if (logLevel >= LogLevel.Error && _firstErrors.Count < FirstErrorsLength)
                {
                    _firstErrors.Add((_received, remembered));
                }

                _tail.Enqueue(remembered);
                if (_tail.Count > TailLength)
                {
                    _tail.Dequeue();
                }
            }
        }

        public (string[] FirstErrors, string[] Tail) Read()
        {
            lock (_gate)
            {
                // The tail holds the lines numbered after this one; an error among them is printed there.
                long beforeTail = _received - _tail.Count;
                return ([.. _firstErrors.Where(error => error.Number <= beforeTail).Select(static error => error.Line)], [.. _tail]);
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
