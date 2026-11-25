using Microsoft.Extensions.Logging;
using Shouldly;
using Trellis.ServiceDefaults;
using Xunit;

namespace Trellis.UiTests;

/// <summary>
/// What the browser tests decide before a browser or an application exists, so these run on every
/// <c>dotnet test</c>, the SDK container's included: where the application comes from, what the fixture
/// does with that, and what a failed start leaves for the reader. They are here for what they prove, not to
/// keep a run green: a run whose browser tests all skip passes without them.
/// </summary>
public sealed class UiTestConfigurationTests
{
    /// <summary>What the stand-in start of the resolver theory hands back; nothing listens there.</summary>
    private const string StartedAddress = "http://127.0.0.1:43117";

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
    /// The whole decision. Without the opt-in nothing starts, in a container or not. A named address is used
    /// wherever the run is. Without one the AppHost is started on a host and refused in a container, where it
    /// cannot orchestrate (ADR-0008). The next theory holds what the fixture does with each answer.
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

    /// <summary>
    /// What the fixture does with the decision, a row per source (the positive control of "an ordinary test
    /// run starts nothing"): a test that asks before its guard, or in a container with no address, fails
    /// with the sentence that says why, a named address is checked before it is handed out, and only the
    /// AppHost source calls the start. The start is a stand-in that counts its calls and returns a fixed
    /// address, and every row names its source: no row reads the environment, and none can start an AppHost,
    /// on any machine or in CI's browser steps, where RUN_UI_TESTS=1.
    /// </summary>
    [Theory]
    [InlineData(AppSource.Refused, null, null, "before its RUN_UI_TESTS guard", 0)]
    [InlineData(AppSource.Refused, "http://localhost:5170", null, "before its RUN_UI_TESTS guard", 0)]
    [InlineData(AppSource.Unreachable, null, null, AppHostFixture.InContainerMessage, 0)]
    [InlineData(AppSource.Named, "localhost:5170", null, "UI_TESTS_BASE_URL is 'localhost:5170', which is not an absolute http or https address", 0)]
    [InlineData(AppSource.Named, "http://localhost:5170", "http://localhost:5170", null, 0)]
    [InlineData(AppSource.AppHost, null, StartedAddress, null, 1)]
    internal async Task Only_the_AppHost_source_starts_anything_a_named_address_is_checked_and_every_other_source_fails_saying_why(
        AppSource source, string? namedBaseUrl, string? address, string? refusal, int starts)
    {
        int started = 0;
        Task<string> Start()
        {
            started++;
            return Task.FromResult(StartedAddress);
        }

        if (refusal is null)
        {
            (await AppHostFixture.ResolveAsync(source, namedBaseUrl, Start)).ShouldBe(address);
        }
        else
        {
            InvalidOperationException refused = await Should.ThrowAsync<InvalidOperationException>(
                () => AppHostFixture.ResolveAsync(source, namedBaseUrl, Start));
            refused.Message.ShouldContain(refusal);
        }

        started.ShouldBe(starts);
    }

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
            // The resources Describe tails, by the constants it reads: a rename moves both sides.
            using AppLogs logs = new(folder);
            ILogger migrations = logs.CreateLogger($"Acme.Shop.AppHost.Resources.{ServiceNames.Migrations}");
            for (int line = 1; line <= 20; line++)
            {
                Print(migrations, LogLevel.Information, $"line {line}");
            }

            Print(logs.CreateLogger($"Acme.Shop.AppHost.Resources.{ServiceNames.Api}"), LogLevel.Error, "Host terminated unexpectedly", new InvalidOperationException("no signing key"));

            string message = AppHostFixture.Describe(
                $"Stopped waiting for resource '{ServiceNames.Api}'.",
                [(ServiceNames.Migrations, "Finished, exit code 0"), (ServiceNames.Api, "FailedToStart")],
                logs);

            message.ShouldContain($"did not come up for the browser tests: Stopped waiting for resource '{ServiceNames.Api}'.");
            message.ShouldContain($"Resources: {ServiceNames.Migrations} = Finished, exit code 0; {ServiceNames.Api} = FailedToStart");
            // The last fifteen lines of a resource, not its first ones.
            message.ShouldNotContain("line 5" + Environment.NewLine);
            message.ShouldContain("line 6" + Environment.NewLine);
            message.ShouldContain("line 20" + Environment.NewLine);
            message.ShouldContain("Host terminated unexpectedly");
            message.ShouldContain("no signing key");
            message.ShouldContain($"Last lines of {ServiceNames.Web}: (it printed nothing)");
            message.ShouldContain(folder);

            // Every line is on disk while the application still runs: a cancelled job keeps what was printed.
            ReadWhileOpen(Path.Combine(folder, $"{ServiceNames.Migrations}.log")).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(20);
            ReadWhileOpen(Path.Combine(folder, $"{ServiceNames.Api}.log")).ShouldContain("no signing key");
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
