using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Trellis.UiTests;

/// <summary>
/// What the browser tests decide before a browser or an application exists, so these run on every
/// <c>dotnet test</c>, the SDK container's included: where the application comes from, and what a failed
/// start leaves for the reader. They also keep the assembly from reporting "zero tests ran" (which fails the
/// test platform) when every browser test is skipped.
/// </summary>
public sealed class UiTestConfigurationTests
{
    /// <summary>
    /// The address a run names is checked when the first test asks for it, and a wrong one fails every browser
    /// test with this sentence instead of a navigation error per test.
    /// </summary>
    [Theory]
    [InlineData("http://localhost:5170", false)]
    [InlineData("https://localhost:7170", false)]
    [InlineData("localhost:5170", true)]
    [InlineData("/login", true)]
    [InlineData("ftp://localhost", true)]
    public void A_named_address_is_an_absolute_http_address_or_the_run_says_what_is_wrong_with_it(string named, bool refused)
    {
        string? problem = UiTestEnvironment.ProblemWith(named);

        if (refused)
        {
            problem.ShouldNotBeNull().ShouldContain($"UI_TESTS_BASE_URL is '{named}'");
        }
        else
        {
            problem.ShouldBeNull();
        }
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("http://localhost:5170", "http://localhost:5170")]
    [InlineData("http://localhost:5170/", "http://localhost:5170")]
    [InlineData(" https://localhost:7170/ ", "https://localhost:7170")]
    public void An_empty_address_names_nothing_and_a_named_one_loses_its_trailing_slash(string? value, string? named)
        => UiTestEnvironment.Named(value).ShouldBe(named);

    /// <summary>
    /// The whole decision. Without the opt-in nothing starts, in a container or not (the positive control of
    /// "an ordinary test run starts nothing"). A named address is used wherever the run is. Without one the
    /// AppHost is started on a host and refused in a container, where it cannot orchestrate (ADR-0008).
    /// </summary>
    [Theory]
    [InlineData(false, null, false, AppSource.Refused)]
    [InlineData(false, "http://localhost:5170", false, AppSource.Refused)]
    [InlineData(false, null, true, AppSource.Refused)]
    [InlineData(false, "http://localhost:5170", true, AppSource.Refused)]
    [InlineData(true, "http://localhost:5170", false, AppSource.Named)]
    [InlineData(true, "http://localhost:5170", true, AppSource.Named)]
    [InlineData(true, null, false, AppSource.AppHost)]
    [InlineData(true, null, true, AppSource.Unreachable)]
    internal void Nothing_starts_without_the_opt_in_or_in_a_container_and_a_named_address_is_used_instead_of_the_AppHost(
        bool enabled, string? namedBaseUrl, bool inContainer, AppSource source)
        => AppHostFixture.SourceFor(enabled, namedBaseUrl, inContainer).ShouldBe(source);

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("1", true)]
    public void The_run_is_in_a_container_when_dotnet_says_so(string? value, bool inContainer)
        => UiTestEnvironment.SaysContainer(value).ShouldBe(inContainer);

    [Theory]
    [InlineData("Acme.Shop.AppHost.Resources.api", "api")]
    [InlineData("Acme.Shop.AppHost.Resources.postgres-pgadmin", "postgres-pgadmin")]
    [InlineData("Aspire.Hosting.Dcp.dcpctrl", AppLogs.AppHost)]
    [InlineData("Microsoft.Hosting.Lifetime", AppLogs.AppHost)]
    [InlineData("Acme.Shop.AppHost.Resources.", AppLogs.AppHost)]
    public void A_resources_lines_go_to_a_file_of_its_name_and_every_other_line_to_the_AppHosts(string category, string sink)
        => AppLogs.SinkNameOf(category).ShouldBe(sink);

    [Fact]
    public void A_failed_start_names_each_resources_state_its_last_lines_and_the_folder_that_holds_them_all()
    {
        string folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
        try
        {
            using AppLogs logs = new(folder);
            ILogger migrations = logs.CreateLogger("Acme.Shop.AppHost.Resources.migrations");
            for (int line = 1; line <= 20; line++)
            {
                Print(migrations, LogLevel.Information, $"line {line}");
            }

            Print(logs.CreateLogger("Acme.Shop.AppHost.Resources.api"), LogLevel.Error, "Host terminated unexpectedly", new InvalidOperationException("no signing key"));

            string message = AppHostFixture.Describe(
                "Stopped waiting for resource 'api'.",
                [("migrations", "Finished, exit code 0"), ("api", "FailedToStart")],
                logs);

            message.ShouldContain("did not come up for the browser tests: Stopped waiting for resource 'api'.");
            message.ShouldContain("Resources: migrations = Finished, exit code 0; api = FailedToStart");
            // The last fifteen lines of a resource, not its first ones.
            message.ShouldNotContain("line 5" + Environment.NewLine);
            message.ShouldContain("line 6" + Environment.NewLine);
            message.ShouldContain("line 20" + Environment.NewLine);
            message.ShouldContain("Host terminated unexpectedly");
            message.ShouldContain("no signing key");
            message.ShouldContain("Last lines of web: (it printed nothing)");
            message.ShouldContain(folder);

            // Every line is on disk while the application still runs: a cancelled job keeps what was printed.
            ReadWhileOpen(Path.Combine(folder, "migrations.log")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(20);
            ReadWhileOpen(Path.Combine(folder, "api.log")).ShouldContain("no signing key");
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>One console line, as Aspire's forwarder hands it to a logger: the text is the whole message.</summary>
    private static void Print(ILogger logger, LogLevel level, string text, Exception? exception = null)
        => logger.Log(level, default, text, exception, static (state, _) => state);

    /// <summary>Reads a file its writer still holds open (Windows refuses a reader that does not share writing).</summary>
    private static string ReadWhileOpen(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
