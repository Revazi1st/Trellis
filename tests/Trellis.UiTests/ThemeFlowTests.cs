using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;
using Xunit;

namespace Trellis.UiTests;

/// <summary>
/// The theme switch in a real browser: a choice is written as data-theme on &lt;html&gt;, kept across a
/// reload and an enhanced navigation, read back by the switch, let go by System, and held over the
/// operating system's scheme; without theme.js a change is caught and the circuit lives on.
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

    [Fact]
    public async Task A_kept_theme_survives_an_enhanced_navigation_out_of_the_error_page()
    {
        Assert.SkipUnless(UiTestEnvironment.Enabled,
            "Browser UI test — set RUN_UI_TESTS=1 with browsers installed and the app running.");

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        IPage page = await browser.NewPageAsync(new() { IgnoreHTTPSErrors = true });

        await page.GotoAsync($"{UiTestEnvironment.WebBaseUrl}/login");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await page.SelectOptionAsync("select.theme-select", "dark");
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark", new() { Timeout = 15_000 });

        // /Error renders statically with no switch; theme.js in the head puts the kept choice on <html>.
        await page.GotoAsync($"{UiTestEnvironment.WebBaseUrl}/Error");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        await Assertions.Expect(page.Locator("select.theme-select")).ToHaveCountAsync(0);

        // The brand link is an enhanced navigation (to /, which sends the anonymous user on to /login): the
        // new page's <html> carries no data-theme and replaces the attributes; theme.js's enhancedload handler
        // puts the choice back before the circuit starts, so the switch reads Dark.
        await page.ClickAsync("a.brand");
        await page.WaitForURLAsync(
            new Regex("/login(?:\\?.*)?$", RegexOptions.None, TimeSpan.FromSeconds(5)), new() { Timeout = 15_000 });
        await Assertions.Expect(page.Locator("select.theme-select")).ToHaveValueAsync("dark", new() { Timeout = 15_000 });
        await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
    }

    [Fact]
    public async Task Without_theme_js_a_change_is_shown_and_the_circuit_lives_on()
    {
        Assert.SkipUnless(UiTestEnvironment.Enabled,
            "Browser UI test — set RUN_UI_TESTS=1 with browsers installed and the app running.");

        using IPlaywright playwright = await Playwright.CreateAsync();
        await using IBrowser browser = await playwright.Chromium.LaunchAsync(new() { Headless = true });
        IPage page = await browser.NewPageAsync(new() { IgnoreHTTPSErrors = true });
        // A host that does not load the script: window.appTheme never exists, so both interop calls fail.
        await page.RouteAsync("**/_content/Trellis.UI.Common/theme.js", static route => route.AbortAsync());

        await page.GotoAsync($"{UiTestEnvironment.WebBaseUrl}/login");
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        // The first change's write fails; the circuit renders it all the same. An uncaught failure would end
        // the circuit there, so the second change would never render and the failure bar would show.
        await page.SelectOptionAsync("select.theme-select", "dark");
        await Assertions.Expect(page.Locator("select.theme-select option[value='dark'][selected]"))
            .ToHaveCountAsync(1, new() { Timeout = 15_000 });
        await page.SelectOptionAsync("select.theme-select", "light");
        await Assertions.Expect(page.Locator("select.theme-select option[value='light'][selected]"))
            .ToHaveCountAsync(1, new() { Timeout = 15_000 });
        await Assertions.Expect(page.Locator("#blazor-error-ui")).ToBeHiddenAsync();
        await Assertions.Expect(page.Locator("html[data-theme]")).ToHaveCountAsync(0);
    }

    private static Task<string> BodyBackgroundAsync(IPage page)
        => page.EvaluateAsync<string>("() => getComputedStyle(document.body).backgroundColor");
}
