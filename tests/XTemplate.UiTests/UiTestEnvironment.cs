namespace XTemplate.UiTests;

/// <summary>Opt-in switch + configuration for the browser-driven tests.</summary>
internal static class UiTestEnvironment
{
    /// <summary>Browser tests need real browsers and a running app, so they are opt-in via RUN_UI_TESTS=1.</summary>
    public static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_UI_TESTS"), "1", StringComparison.Ordinal);

    /// <summary>Base address of the running Blazor web app.</summary>
    public static string WebBaseUrl =>
        Environment.GetEnvironmentVariable("UI_TESTS_BASE_URL") ?? "https://localhost:7170";

    public const string SeededAdminEmail = "admin@xtemplate.dev";
    public const string SeededAdminPassword = "Admin123!";
}
