using Microsoft.Playwright;
using Shouldly;
using Xunit;

namespace XTemplate.UiTests;

/// <summary>
/// Drives the Blazor UI in a real browser: sign in as the seeded admin, then land on the invoice list.
/// Opt-in (RUN_UI_TESTS=1) — needs installed browsers and the app running (`aspire run`).
/// </summary>
public sealed class LoginFlowTests
{
    [Fact]
    public async Task Signing_in_lands_on_the_invoice_list()
    {
        Assert.SkipUnless(UiTestEnvironment.Enabled,
            "Browser UI test — set RUN_UI_TESTS=1 with browsers installed and the app running.");

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        IPage page = await browser.NewPageAsync(new() { IgnoreHTTPSErrors = true });

        await page.GotoAsync($"{UiTestEnvironment.WebBaseUrl}/login");

        await page.FillAsync("input[autocomplete='username']", UiTestEnvironment.SeededAdminEmail);
        await page.FillAsync("input[autocomplete='current-password']", UiTestEnvironment.SeededAdminPassword);
        await page.ClickAsync("button[type='submit']");

        await page.WaitForURLAsync("**/invoices", new() { Timeout = 15_000 });
        (await page.TitleAsync()).ShouldContain("Invoices");
    }
}

/// <summary>
/// Always-on guard so the assembly never reports "zero tests ran" (which fails the test platform)
/// when the browser tests are skipped.
/// </summary>
public sealed class UiTestConfigurationTests
{
    [Fact]
    public void Base_url_is_absolute_so_the_browser_can_navigate_to_it()
        => Uri.TryCreate(UiTestEnvironment.WebBaseUrl, UriKind.Absolute, out _).ShouldBeTrue();
}
