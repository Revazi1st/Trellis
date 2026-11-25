using System.Globalization;
using System.Text;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trellis.ServiceDefaults;
using Trellis.TestSupport.AppHost;
using Xunit;

[assembly: AssemblyFixture(typeof(Trellis.UiTests.AppHostFixture))]

namespace Trellis.UiTests;

/// <summary>
/// The application the browser tests drive, one for the whole test run. Unless UI_TESTS_BASE_URL names an app
/// that is already running, the first test past its RUN_UI_TESTS guard starts the AppHost (PostgreSQL, the
/// migration worker, the API and the web host, as <c>aspire run</c> does, on ports of Aspire's choosing) and
/// every test then gets the web host's address from <see cref="WebBaseUrlAsync"/>. The database is a
/// throwaway one (<c>UseThrowawayDatabase</c>): a container and a database of this run's own, seeded by the
/// migration worker and removed with the run, never the one <c>aspire run</c> keeps.
/// <para>
/// Nothing starts in the constructor: xUnit creates an assembly fixture before any test runs, and a run
/// without RUN_UI_TESTS=1 (the SDK container, an ordinary <c>dotnet test</c>) skips every browser test, so
/// it must start nothing. The start ends as soon as a resource the web host needs fails (the web host
/// itself and everything it waits for: <c>RunUnlessOneFailsAsync</c> over <c>NeededBy</c>), and otherwise
/// at <see cref="StartTimeout"/>. A start that fails is kept: every test fails with the same message, which
/// holds the reason, each resource's state, and the first error lines and the last lines of the migration
/// job, the API, the web host and the AppHost; the whole of what each resource printed is in
/// <see cref="UiTestEnvironment.LogRoot"/>.
/// </para>
/// <para>
/// It needs what the Aspire E2E needs (ADR-0008): a .NET SDK on the host or CI, and Docker. In a container
/// (the dev container) the AppHost is not started: the run names the web host with UI_TESTS_BASE_URL, or
/// every test fails at once saying so. Run the project, not the solution: beside the Testcontainers suites
/// the AppHost's processes race them for host ports.
/// </para>
/// </summary>
public sealed class AppHostFixture : IAsyncDisposable
{
    /// <summary>How long the start may take, image pulls included, before it fails with the logs.</summary>
    internal static readonly TimeSpan StartTimeout = TimeSpan.FromMinutes(5);

    private readonly Lazy<Task<string>> _webBaseUrl;
    private DistributedApplication? _app;
    private AppLogs? _logs;

    public AppHostFixture() => _webBaseUrl = new Lazy<Task<string>>(() => ResolveAsync(
        SourceFor(UiTestEnvironment.Enabled, UiTestEnvironment.NamedBaseUrl, UiTestEnvironment.InContainer),
        UiTestEnvironment.NamedBaseUrl,
        StartAsync));

    /// <summary>
    /// The web host's address, without a trailing slash. Call it after the test's RUN_UI_TESTS guard: the
    /// first call starts the application (or takes the address UI_TESTS_BASE_URL names), every later one
    /// awaits the same start.
    /// </summary>
    public Task<string> WebBaseUrlAsync() => _webBaseUrl.Value;

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_app is not null)
            {
                await _app.DisposeAsync();
            }
        }
        finally
        {
            _logs?.Dispose();
        }
    }

    /// <summary>
    /// What a test gets when the run is in a container and names no address. The AppHost is not started
    /// there: its projects would run in the container and PostgreSQL beside it on the host, the two cannot
    /// reach each other, and the start would fail on that instead of on anything the tests are about.
    /// </summary>
    internal const string InContainerMessage =
        "The browser tests cannot start the application from inside a container (the dev container, the SDK container): "
        + "the AppHost would run the projects in the container and PostgreSQL beside it on the host, and the two cannot reach each other (ADR-0008). "
        + "Run the web host from its launch profile and name it, UI_TESTS_BASE_URL=http://localhost:5170, "
        + "or run the tests on a machine with a .NET SDK.";

    /// <summary>What a test gets when it asks for the application without RUN_UI_TESTS=1.</summary>
    internal const string AskedBeforeTheGuardMessage =
        "A browser test asked for the application before its RUN_UI_TESTS guard (Assert.SkipUnless(UiTestEnvironment.Enabled, …)): "
        + "without RUN_UI_TESTS=1 nothing may start.";

    /// <summary>
    /// Where the application comes from, given the opt-in, the address the environment names and whether the
    /// run is in a container. A named address wins everywhere; without one a container starts nothing.
    /// </summary>
    internal static AppSource SourceFor(bool enabled, string? namedBaseUrl, bool inContainer)
    {
        if (!enabled)
        {
            return AppSource.Refused;
        }

        if (namedBaseUrl is not null)
        {
            return AppSource.Named;
        }

        return inContainer ? AppSource.Unreachable : AppSource.AppHost;
    }

    /// <summary>
    /// The message of a start that failed: why, each resource's state, then for the resources the
    /// application is made of and for the AppHost itself their first error lines (when they printed any
    /// before their last lines) and their last lines, and where every line is.
    /// </summary>
    internal static string Describe(string reason, IEnumerable<(string Resource, string State)> states, AppLogs logs)
    {
        StringBuilder message = new();
        message.Append(CultureInfo.InvariantCulture, $"The AppHost did not come up for the browser tests: {reason}").AppendLine();
        message.Append(CultureInfo.InvariantCulture, $"Resources: {string.Join("; ", states.Select(static s => $"{s.Resource} = {s.State}"))}").AppendLine();
        foreach (string sink in new[] { ServiceNames.Migrations, ServiceNames.Api, ServiceNames.Web, AppLogs.AppHost })
        {
            IReadOnlyList<string> errors = logs.FirstErrors(sink);
            if (errors.Count > 0)
            {
                message.Append(CultureInfo.InvariantCulture, $"First error lines of {sink}:").AppendLine();
                AppendIndented(message, errors);
            }

            IReadOnlyList<string> tail = logs.Tail(sink);
            message.Append(CultureInfo.InvariantCulture, $"Last lines of {sink}:{(tail.Count == 0 ? " (it printed nothing)" : string.Empty)}").AppendLine();
            AppendIndented(message, tail);
        }

        message.Append(CultureInfo.InvariantCulture, $"Every line each resource printed is in {logs.Folder} (one file per resource; UI_TESTS_LOGS names the folder).");
        return message.ToString();

        static void AppendIndented(StringBuilder text, IReadOnlyList<string> lines)
        {
            foreach (string line in lines)
            {
                // A line with an exception is two: the exception's first line goes under its text.
                text.Append("  ").AppendLine(line.Replace(Environment.NewLine, Environment.NewLine + "  ", StringComparison.Ordinal));
            }
        }
    }

    /// <summary>
    /// What the fixture does with where the application comes from: hands out the named address once it is
    /// checked, hands out the address of the AppHost <paramref name="start"/> starts, or fails with the
    /// sentence that says why nothing started. Only <see cref="AppSource.AppHost"/> calls
    /// <paramref name="start"/>: the fixture passes <see cref="StartAsync"/>, and
    /// <see cref="UiTestConfigurationTests"/> a stand-in that starts nothing. A refusal is a faulted task,
    /// which the fixture's <see cref="Lazy{T}"/> keeps, so every test fails with the same sentence.
    /// </summary>
    internal static async Task<string> ResolveAsync(AppSource source, string? namedBaseUrl, Func<Task<string>> start)
        => source switch
        {
            AppSource.Named => UiTestEnvironment.ProblemWith(namedBaseUrl ?? string.Empty) is { } problem
                ? throw new InvalidOperationException(problem)
                : namedBaseUrl!,
            AppSource.AppHost => await start(),
            AppSource.Unreachable => throw new InvalidOperationException(InContainerMessage),
            _ => throw new InvalidOperationException(AskedBeforeTheGuardMessage),
        };

    /// <summary>
    /// Hands the AppHost's lines to <paramref name="logs"/>: every category at Information, whatever the
    /// AppHost's own appsettings filter, because the files are read after a failure; and the HTTP client's
    /// categories (<see cref="HttpClientCategory"/>) at Warning. The health checks request every host and tile
    /// over and over through that client, which at Information logs each request at its start, its send, its
    /// answer and its end: that filled <c>apphost.log</c> and its last lines with nothing a failed start is
    /// explained by. A check that fails is still in the file, as the health check's own error.
    /// </summary>
    internal static void CaptureInto(ILoggingBuilder logging, AppLogs logs)
        => logging.AddProvider(logs)
            .AddFilter<AppLogs>(category: null, LogLevel.Information)
            .AddFilter<AppLogs>(HttpClientCategory, LogLevel.Warning);

    /// <summary>The prefix of the categories <c>IHttpClientFactory</c>'s clients log under, one per client and handler.</summary>
    internal const string HttpClientCategory = "System.Net.Http.HttpClient";

    private async Task<string> StartAsync()
    {
        using CancellationTokenSource timeout = new(StartTimeout);
        CancellationToken ct = timeout.Token;
        AppLogs logs = _logs = new AppLogs(UiTestEnvironment.LogRoot);
        string[] resources = [];
        DistributedApplication? app = null;
        try
        {
            // Before anything prints: the message of a start refused before its first line then names a
            // folder that exists, and a folder that cannot be created fails here, with that message.
            Directory.CreateDirectory(logs.Folder);
            IDistributedApplicationTestingBuilder appHost =
                await DistributedApplicationTestingBuilder.CreateAsync<Projects.Trellis_AppHost>(ct);
            appHost.UseThrowawayDatabase();
            appHost.Services.AddLogging(logging => CaptureInto(logging, logs));
            resources = [.. appHost.Resources.Select(static resource => resource.Name)];
            // The web host and everything it waits for: the API, the migration job, the database and its
            // server. Not the tool tiles: a pgAdmin that crashes does not stop the browser tests.
            string[] needed = AppHostTestingExtensions.NeededBy(appHost.Resources, ServiceNames.Web);

            DistributedApplication started = app = await appHost.BuildAsync(ct);

            // StartAsync does not return while a resource waits for a dependency, and a resource whose
            // dependency has exited goes on waiting (CI saw the web host wait for an API that had crashed at
            // boot, until the time limit). So the start and the two waits each run under a watch of the needed
            // resources: the first of them that fails to start, or exits with a code other than 0, ends the
            // start at once with its own sentence. Healthy is each host's own /health answering (the AppHost's
            // WithHttpHealthCheck), and the API starts only once the migration job has migrated, seeded and
            // exited with 0. The waits keep StopOnResourceUnavailable, Aspire's own stop for the resource a
            // wait names, and when that stop comes first the helper still ends with the resource's sentence;
            // the watch covers the resources behind it. The time limit covers what is left: a resource that
            // keeps running and never becomes healthy.
            await started.RunUnlessOneFailsAsync(needed, started.StartAsync, ct);
            await started.RunUnlessOneFailsAsync(needed, token => started.ResourceNotifications.WaitForResourceHealthyAsync(
                ServiceNames.Api, WaitBehavior.StopOnResourceUnavailable, token), ct);
            await started.RunUnlessOneFailsAsync(needed, token => started.ResourceNotifications.WaitForResourceHealthyAsync(
                ServiceNames.Web, WaitBehavior.StopOnResourceUnavailable, token), ct);

            string address = UiTestEnvironment.WithoutTrailingSlash(started.GetEndpoint(ServiceNames.Web).ToString());
            _app = started;
            return address;
        }
        catch (Exception exception)
        {
            string reason = timeout.IsCancellationRequested
                ? string.Create(CultureInfo.InvariantCulture, $"not healthy within {StartTimeout.TotalMinutes:0} minutes.")
                : exception.Message;
            string message = Describe(reason, resources.Select(name => (name, StateOf(app, name))), logs);
            if (app is not null)
            {
                // The states are read above, before the stop. A stop that throws must not replace the start's
                // message, which is what every browser test fails with.
                try
                {
                    await app.DisposeAsync();
                }
                catch (Exception stopFailure)
                {
                    message += Environment.NewLine + $"Stopping what had started failed as well: {stopFailure.Message}";
                }
            }

            throw new InvalidOperationException(message, exception);
        }
    }

    private static string StateOf(DistributedApplication? app, string resource)
    {
        if (app is null || !app.ResourceNotifications.TryGetCurrentState(resource, out ResourceEvent? current))
        {
            return "not started";
        }

        CustomResourceSnapshot snapshot = current.Snapshot;
        string state = snapshot.State?.Text ?? "no state";
        string health = snapshot.HealthStatus is { } status ? $", {status}" : string.Empty;
        string exit = snapshot.ExitCode is { } code ? string.Create(CultureInfo.InvariantCulture, $", exit code {code}") : string.Empty;
        return state + health + exit;
    }
}

/// <summary>Where a run's application comes from.</summary>
internal enum AppSource
{
    /// <summary>RUN_UI_TESTS is not 1: nothing is started and nothing is named.</summary>
    Refused,

    /// <summary>UI_TESTS_BASE_URL names an application that is already running.</summary>
    Named,

    /// <summary>The fixture starts the AppHost.</summary>
    AppHost,

    /// <summary>
    /// The run is in a container and names no address: the AppHost cannot orchestrate there, so nothing is
    /// started and every browser test fails at once with <see cref="AppHostFixture.InContainerMessage"/>.
    /// </summary>
    Unreachable,
}
