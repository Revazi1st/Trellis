using Microsoft.Playwright;
using Shouldly;
using Xunit;

namespace Trellis.UiTests;

/// <summary>
/// The theme switch in a real browser: a choice is written as data-theme on &lt;html&gt;, kept across a
/// reload and read back by the switch, let go by System, and held over the operating system's scheme.
/// The sign-in page is enough: the switch sits outside AuthorizeView. Opt-in (RUN_UI_TESTS=1), like
/// <see cref="LoginFlowTests"/>: needs installed browsers and the app running (`aspire run`). This flow owns
/// the switch's read and write, which the render tests in ApiTests cannot reach.
/// </summary>
public sealed class ThemeFlowTests
{
    [Fact]
    public async Task A_chosen_theme_is_kept_across_a_reload_and_System_lets_it_go()
    {
        Assert.SkipUnless(UiTestEnvironment.Enabled,
            "Browser UI test — set RUN_UI_TESTS=1 with browsers installed and the app running.");

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        IPage page = await browser.NewPageAsync(new() { IgnoreHTTPSErrors = true });

        await page.GotoAsync($"{UiTestEnvironment.WebBaseUrl}/login");
        // Wait for the Blazor Server circuit to attach: a change made before it is never handled.
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        await page.SelectOptionAsync("select.theme-select", "dark");
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark", new() { Timeout = 15_000 });

        // Kept: theme.js puts it back from the head before the first paint, and the switch reads it once interactive.
        await page.ReloadAsync();
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark", new() { Timeout = 15_000 });
        await Assertions.Expect(page.Locator("select.theme-select")).ToHaveValueAsync("dark", new() { Timeout = 15_000 });

        await page.SelectOptionAsync("select.theme-select", "system");
        await Assertions.Expect(page.Locator("html[data-theme]")).ToHaveCountAsync(0, new() { Timeout = 15_000 });
    }

    [Fact]
    public async Task Dark_holds_over_a_light_operating_system()
    {
        Assert.SkipUnless(UiTestEnvironment.Enabled,
            "Browser UI test — set RUN_UI_TESTS=1 with browsers installed and the app running.");

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        IPage page = await browser.NewPageAsync(new() { IgnoreHTTPSErrors = true });
        await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });

        await page.GotoAsync($"{UiTestEnvironment.WebBaseUrl}/login");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Assertions.Expect(page.Locator("html[data-theme]")).ToHaveCountAsync(0);
        string light = await BodyBackgroundAsync(page);

        await page.SelectOptionAsync("select.theme-select", "dark");
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark", new() { Timeout = 15_000 });

        string dark = await BodyBackgroundAsync(page);
        dark.ShouldNotBe(light, StringComparer.Ordinal,
            "data-theme=\"dark\" sets color-scheme: dark, so light-dark() takes the dark value over the system's light scheme");
    }

    private static Task<string> BodyBackgroundAsync(IPage page)
        => page.EvaluateAsync<string>("() => getComputedStyle(document.body).backgroundColor");
}
