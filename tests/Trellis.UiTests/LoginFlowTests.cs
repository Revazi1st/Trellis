using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;
using Xunit;

namespace Trellis.UiTests;

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
        // Wait for the Blazor Server circuit to attach — values typed before it are lost.
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await page.FillAsync("input[autocomplete='username']", UiTestEnvironment.SeededAdminEmail);
        await page.FillAsync("input[autocomplete='current-password']", UiTestEnvironment.SeededAdminPassword);
        await page.ClickAsync("button[type='submit']");

        await page.WaitForURLAsync("**/invoices", new() { Timeout = 15_000 });
        // WaitForURLAsync returns the moment the circuit navigates, but Blazor applies <PageTitle>
        // on the NEXT render — reading the title right here can still see "Sign in". Expect() is the
        // auto-retrying assertion; a bare TitleAsync() is a race that passes on a fast agent and
        // fails on a loaded one.
        await Assertions.Expect(page).ToHaveTitleAsync(
            new Regex("Invoices", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5)),
            new() { Timeout = 15_000 });
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
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.FillAsync("input[autocomplete='username']", UiTestEnvironment.SeededAdminEmail);
        await page.FillAsync("input[autocomplete='current-password']", "definitely-not-the-password");
        await page.ClickAsync("button[type='submit']");

        await page.Locator("p.error[role='alert']").WaitForAsync(new() { Timeout = 15_000 });
        page.Url.ShouldContain("/login");
    }

    [Fact]
    public async Task Signing_in_from_a_protected_page_returns_to_it()
    {
        Assert.SkipUnless(UiTestEnvironment.Enabled,
            "Browser UI test — set RUN_UI_TESTS=1 with browsers installed and the app running.");

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        IPage page = await browser.NewPageAsync(new() { IgnoreHTTPSErrors = true });

        // Anonymous on a protected page: sent to sign-in, carrying the page as returnUrl.
        await page.GotoAsync($"{UiTestEnvironment.WebBaseUrl}/invoices/new");
        await page.WaitForURLAsync(
            new Regex("/login\\?returnUrl=invoices%2Fnew$", RegexOptions.None, TimeSpan.FromSeconds(5)),
            new() { Timeout = 15_000 });
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await page.FillAsync("input[autocomplete='username']", UiTestEnvironment.SeededAdminEmail);
        await page.FillAsync("input[autocomplete='current-password']", UiTestEnvironment.SeededAdminPassword);
        await page.ClickAsync("button[type='submit']");

        await page.WaitForURLAsync(
            new Regex("/invoices/new$", RegexOptions.None, TimeSpan.FromSeconds(5)),
            new() { Timeout = 15_000 });
        await page.Locator("h1", new() { HasText = "New invoice" }).WaitForAsync(new() { Timeout = 15_000 });
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
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.FillAsync("input[autocomplete='username']", UiTestEnvironment.SeededAdminEmail);
        await page.FillAsync("input[autocomplete='current-password']", UiTestEnvironment.SeededAdminPassword);
        await page.ClickAsync("button[type='submit']");
        await page.WaitForURLAsync("**/invoices", new() { Timeout = 15_000 });

        // Navigate via the nav link: per-circuit tokens do not survive a full page load (ADR-0007),
        // so a GotoAsync here would bounce back to the login page.
        await page.ClickAsync("a.nav-link:has-text('New')");
        await page.WaitForURLAsync("**/invoices/new", new() { Timeout = 15_000 });
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
        await page.Locator("h1", new() { HasText = customer }).WaitForAsync(new() { Timeout = 15_000 });
    }

    [Fact]
    public async Task An_issued_invoice_heads_the_list_the_back_link_returns_to_and_is_not_overdue()
    {
        Assert.SkipUnless(UiTestEnvironment.Enabled,
            "Browser UI test — set RUN_UI_TESTS=1 with browsers installed and the app running.");

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        IPage page = await browser.NewPageAsync(new() { IgnoreHTTPSErrors = true });

        await page.GotoAsync($"{UiTestEnvironment.WebBaseUrl}/login");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.FillAsync("input[autocomplete='username']", UiTestEnvironment.SeededAdminEmail);
        await page.FillAsync("input[autocomplete='current-password']", UiTestEnvironment.SeededAdminPassword);
        await page.ClickAsync("button[type='submit']");
        await page.WaitForURLAsync("**/invoices", new() { Timeout = 15_000 });

        // Create, add a line, issue due in 30 days: never overdue today. In-app navigation only (ADR-0007).
        await page.ClickAsync("a.nav-link:has-text('New')");
        await page.WaitForURLAsync("**/invoices/new", new() { Timeout = 15_000 });
        string customer = $"Listed {Guid.NewGuid():N}";
        await page.FillAsync("label:has-text('Customer name') input", customer);
        await page.FillAsync("label:has-text('Customer email') input", "listed@test.dev");
        await page.FillAsync("label:has-text('Currency') input", "USD");
        await page.ClickAsync("button[type='submit']");
        await page.Locator("h1", new() { HasText = customer }).WaitForAsync(new() { Timeout = 15_000 });

        await page.FillAsync("label:has-text('Description') input", "Listing check");
        await page.FillAsync("label:has-text('Amount') input", "120");
        await page.ClickAsync("button:has-text('Add line')");
        await page.Locator("td", new() { HasText = "Listing check" }).WaitForAsync(new() { Timeout = 15_000 });
        string dueDate = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        await page.FillAsync("label:has-text('Due date') input", dueDate);
        await page.ClickAsync("button:has-text('Issue invoice')");
        await Assertions.Expect(page.Locator("dl.summary dd").First).ToHaveTextAsync("Issued", new() { Timeout = 15_000 });

        // Back to the list through the page's own link: the newest invoice heads it under All.
        await page.ClickAsync("a.back:has-text('Back to invoices')");
        await page.WaitForURLAsync(
            new Regex("/invoices$", RegexOptions.None, TimeSpan.FromSeconds(5)), new() { Timeout = 15_000 });
        await Assertions.Expect(page.Locator("a.filter[aria-current='page']")).ToHaveTextAsync("All", new() { Timeout = 15_000 });
        ILocator firstRow = page.Locator("table.grid tbody tr").First;
        await Assertions.Expect(firstRow.Locator("td").First).ToHaveTextAsync(customer, new() { Timeout = 15_000 });
        await Assertions.Expect(firstRow.Locator("td").Nth(1)).ToHaveTextAsync("Issued");

        // Not under Overdue: wait for that list to finish loading, then look for the customer.
        await page.ClickAsync("a.filter:has-text('Overdue')");
        await page.WaitForURLAsync("**/invoices?status=Overdue", new() { Timeout = 15_000 });
        await Assertions.Expect(page.Locator("a.filter[aria-current='page']")).ToHaveTextAsync("Overdue", new() { Timeout = 15_000 });
        await Assertions.Expect(page.Locator("p[role='status']")).ToHaveCountAsync(0, new() { Timeout = 15_000 });
        await Assertions.Expect(page.Locator("td", new() { HasText = customer })).ToHaveCountAsync(0);
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
