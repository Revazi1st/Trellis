using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;
using Xunit;

namespace Trellis.UiTests;

/// <summary>
/// The app as a first run shows it, kept as four pictures for a README or a release note: the invoice list on
/// a laptop and one seeded invoice on a phone, each in the light and the dark scheme. Both schemes are taken
/// from one page, one after the other, so a pair shows the same rows.
/// <para>
/// A first run is a claim about the database, so the run has to make it: with UI_TESTS_FIRST_RUN=1 the fact
/// fails unless the list holds exactly the invoices the migration worker seeds, and without it the fact skips.
/// CI's ui-tests job runs this class alone, with the variable, before the other browser tests create their
/// invoices (ci.yml, "Keep pictures of the first run"). Opt-in (RUN_UI_TESTS=1) like every browser test.
/// </para>
/// </summary>
public sealed class FirstRunPicturesTests
{
    private const int Laptop = 1280;
    private const int Phone = 400;

    // A full-page screenshot is as tall as the window at least. The seeded list is short, so its window is
    // too: a picture of three rows above half a screen of nothing is no picture for a README.
    private const int LaptopHeight = 480;
    private const int PhoneHeight = 900;

    /// <summary>The customers of the invoices <c>MigrationWorker.SeedSampleDataAsync</c> writes, in any order.</summary>
    private static readonly string[] SeededCustomers = ["Draft & Co", "Overdue Industries", "Prompt Payers Ltd"];

    /// <summary>The seeded draft: its page shows the most (a line, and the forms a draft offers).</summary>
    private const string InvoiceOnThePhone = "Draft & Co";

    [Fact]
    public async Task The_first_run_is_kept_as_pictures_of_the_list_and_of_one_invoice_in_both_schemes()
    {
        Assert.SkipUnless(UiTestEnvironment.Enabled,
            "Browser UI test — set RUN_UI_TESTS=1 with browsers installed and the app running.");
        Assert.SkipUnless(UiTestEnvironment.FirstRun,
            "Pictures of a first run — set UI_TESTS_FIRST_RUN=1 when the database holds only what the migration worker seeds, and run this class alone.");

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        IPage page = await browser.NewPageAsync(new() { IgnoreHTTPSErrors = true });

        await page.SetViewportSizeAsync(Laptop, LaptopHeight);
        await Session.SignInAsAdminAsync(page);
        // The list has finished loading (no "Loading…" status) before its rows are read.
        await Assertions.Expect(page.Locator("a.filter[aria-current='page']")).ToHaveTextAsync("All", new() { Timeout = 15_000 });
        await Assertions.Expect(page.Locator("p[role='status']")).ToHaveCountAsync(0, new() { Timeout = 15_000 });

        IReadOnlyList<string> customers = await page.Locator("table.grid tbody td[data-th='Customer']").AllInnerTextsAsync();
        customers.ShouldBe(SeededCustomers, StringComparer.Ordinal, ignoreOrder: true,
            customMessage: "the list holds something other than the seeded invoices, so this is not a first run: start from a freshly migrated database and run this class before any other browser test");

        await KeepInBothSchemesAsync(page, "first-run-invoices-1280");

        // Through the row's own link: a full page load would start a signed-out circuit (ADR-0007).
        await page.GetByRole(AriaRole.Link, new() { Name = $"View the invoice for {InvoiceOnThePhone}" }).ClickAsync();
        await page.WaitForURLAsync(
            new Regex("/invoices/[0-9a-fA-F-]{36}$", RegexOptions.None, TimeSpan.FromSeconds(5)),
            new() { Timeout = 15_000 });
        await page.Locator("h1", new() { HasText = InvoiceOnThePhone }).WaitForAsync(new() { Timeout = 15_000 });

        await page.SetViewportSizeAsync(Phone, PhoneHeight);
        await KeepInBothSchemesAsync(page, "first-run-invoice-400");
    }

    /// <summary>
    /// Saves the full page as <paramref name="name"/>-light.png and -dark.png in
    /// <see cref="UiTestEnvironment.ScreenshotRoot"/>, the page itself unchanged between the two but for the
    /// scheme the browser reports, and fails a page that shows an error where its content belongs.
    /// </summary>
    private static async Task KeepInBothSchemesAsync(IPage page, string name)
    {
        Directory.CreateDirectory(UiTestEnvironment.ScreenshotRoot);
        foreach ((ColorScheme scheme, string suffix) in new[] { (ColorScheme.Light, "light"), (ColorScheme.Dark, "dark") })
        {
            await page.EmulateMediaAsync(new() { ColorScheme = scheme });
            string file = Path.Combine(UiTestEnvironment.ScreenshotRoot, $"{name}-{suffix}.png");
            await page.ScreenshotAsync(new() { Path = file, FullPage = true });

            int alerts = await page.Locator("p.error[role='alert']").CountAsync();
            alerts.ShouldBe(0, $"{name} in {suffix} shows an error instead of its content (see {file})");
        }
    }
}
