namespace Trellis.UiTests;

/// <summary>Opt-in switch + configuration for the browser-driven tests.</summary>
internal static class UiTestEnvironment
{
    /// <summary>Browser tests need real browsers and a running app, so they are opt-in via RUN_UI_TESTS=1.</summary>
    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_UI_TESTS"), "1", StringComparison.Ordinal);

    /// <summary>Base address of the running Blazor web app.</summary>
    public static string WebBaseUrl =>
        Environment.GetEnvironmentVariable("UI_TESTS_BASE_URL") ?? "https://localhost:7170";

    /// <summary>
    /// Where <see cref="DesignSystemProofTests"/> saves its screenshots: UI_TESTS_SCREENSHOTS, else
    /// TestResults/screenshots under the working directory, which under <c>dotnet test</c> is the build output
    /// (artifacts/bin/Trellis.UiTests/debug or release). CI names a folder in the checkout and uploads it.
    /// </summary>
    public static string ScreenshotRoot =>
        Path.GetFullPath(Environment.GetEnvironmentVariable("UI_TESTS_SCREENSHOTS") is { Length: > 0 } directory
            ? directory
            : Path.Combine("TestResults", "screenshots"));

    public const string SeededAdminEmail = "admin@example.com";
    public const string SeededAdminPassword = "Admin123!";
}
