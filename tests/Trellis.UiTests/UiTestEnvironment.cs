namespace Trellis.UiTests;

/// <summary>Opt-in switch + configuration for the browser-driven tests.</summary>
internal static class UiTestEnvironment
{
    /// <summary>Browser tests need a real browser and start the whole application, so they are opt-in via RUN_UI_TESTS=1.</summary>
    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_UI_TESTS"), "1", StringComparison.Ordinal);

    /// <summary>
    /// The run says the database holds only what the migration worker seeds: UI_TESTS_FIRST_RUN=1, which
    /// <see cref="FirstRunPicturesTests"/> needs and CI sets on the step that runs it alone and first.
    /// </summary>
    public static bool FirstRun =>
        string.Equals(Environment.GetEnvironmentVariable("UI_TESTS_FIRST_RUN"), "1", StringComparison.Ordinal);

    /// <summary>
    /// The address of a web app that is already running, when UI_TESTS_BASE_URL names one, without a trailing
    /// slash; <see langword="null"/> otherwise, and <see cref="AppHostFixture"/> starts the application itself.
    /// </summary>
    public static string? NamedBaseUrl => Named(Environment.GetEnvironmentVariable("UI_TESTS_BASE_URL"));

    /// <summary>
    /// The test process runs in a container (the dev container, the SDK container): .NET's own images say so
    /// with DOTNET_RUNNING_IN_CONTAINER. The AppHost cannot orchestrate from there (ADR-0008), so
    /// <see cref="AppHostFixture"/> starts nothing and asks for UI_TESTS_BASE_URL instead.
    /// </summary>
    public static bool InContainer => SaysContainer(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"));

    /// <summary>
    /// Where <see cref="DesignSystemProofTests"/> and <see cref="FirstRunPicturesTests"/> save their
    /// screenshots: UI_TESTS_SCREENSHOTS, else
    /// TestResults/screenshots under the working directory, which under <c>dotnet test</c> is the build output
    /// (artifacts/bin/Trellis.UiTests/debug or release). CI names a folder in the checkout and uploads it.
    /// </summary>
    public static string ScreenshotRoot => FolderFrom("UI_TESTS_SCREENSHOTS", "screenshots");

    /// <summary>
    /// Where <see cref="AppHostFixture"/> writes what each resource of the application it started printed, one
    /// file per resource: UI_TESTS_LOGS, else TestResults/applogs beside the screenshots.
    /// </summary>
    public static string LogRoot => FolderFrom("UI_TESTS_LOGS", "applogs");

    public const string SeededAdminEmail = "admin@example.com";
    public const string SeededAdminPassword = "Admin123!";

    /// <summary>What <see cref="NamedBaseUrl"/> makes of the variable's value: nothing of an empty one.</summary>
    internal static string? Named(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : WithoutTrailingSlash(value.Trim());

    /// <summary>What <see cref="InContainer"/> makes of the variable's value: .NET writes <c>true</c> and reads <c>1</c> too.</summary>
    internal static bool SaysContainer(string? value)
        => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "1", StringComparison.Ordinal);

    /// <summary>
    /// What is wrong with a named address, or <see langword="null"/>: the browser navigates to
    /// <c>$"{address}/login"</c>, so it is an absolute http or https address. (On Linux and macOS
    /// <c>/login</c> parses as an absolute file address, hence the scheme.)
    /// </summary>
    internal static string? ProblemWith(string namedBaseUrl)
        => Uri.TryCreate(namedBaseUrl, UriKind.Absolute, out Uri? address) && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps)
            ? null
            : $"UI_TESTS_BASE_URL is '{namedBaseUrl}', which is not an absolute http or https address (http://localhost:5170, say): the browser cannot navigate to it.";

    /// <summary>The tests write <c>$"{address}/login"</c>, and Aspire hands out an address that ends in a slash.</summary>
    internal static string WithoutTrailingSlash(string address) => address.TrimEnd('/');

    private static string FolderFrom(string variable, string defaultName)
        => Path.GetFullPath(Environment.GetEnvironmentVariable(variable) is { Length: > 0 } directory
            ? directory
            : Path.Combine("TestResults", defaultName));
}
