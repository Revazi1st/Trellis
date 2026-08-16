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

    [Fact]
    public async Task Wrong_password_shows_an_error_and_stays_on_the_login_page()
    {
        Assert.SkipUnless(UiTestEnvironment.Enabled,
            "Browser UI test — set RUN_UI_TESTS=1 with browsers installed and the app running.");

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        IPage page = await browser.NewPageAsync(new() { IgnoreHTTPSErrors = true });

        await page.GotoAsync($"{UiTestEnvironment.WebBaseUrl}/login");
        await page.FillAsync("input[autocomplete='username']", UiTestEnvironment.SeededAdminEmail);
        await page.FillAsync("input[autocomplete='current-password']", "definitely-not-the-password");
        await page.ClickAsync("button[type='submit']");

        await page.Locator("p.error[role='alert']").WaitForAsync(new() { Timeout = 15_000 });
        page.Url.ShouldContain("/login");
    }

    [Fact]
    public async Task Creating_a_draft_invoice_lands_on_its_detail_page()
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

        await page.GotoAsync($"{UiTestEnvironment.WebBaseUrl}/invoices/new");
        string customer = $"Browser Test {Guid.NewGuid():N}";
        await page.FillAsync("label:has-text('Customer name') input", customer);
        await page.FillAsync("label:has-text('Customer email') input", "browser@test.dev");
        await page.FillAsync("label:has-text('Currency') input", "USD");
        await page.ClickAsync("button[type='submit']");

        // The success path navigates to /invoices/{guid} — the detail page shows the customer as the heading.
        await page.WaitForURLAsync(
            new System.Text.RegularExpressions.Regex(
                "/invoices/[0-9a-fA-F-]{36}$", System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(5)),
            new() { Timeout = 15_000 });
        (await page.Locator("h1").InnerTextAsync()).ShouldBe(customer);
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
