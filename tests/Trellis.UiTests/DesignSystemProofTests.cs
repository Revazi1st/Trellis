using System.Text.RegularExpressions;
using Microsoft.Playwright;
using Shouldly;
using Xunit;

namespace Trellis.UiTests;

/// <summary>
/// The design system's proof (docs/design-system.md, Proof) in a real browser: every page at 400 and 1280 px
/// wide in the light and the dark scheme, saved as a full-page screenshot for a reviewer to open (CI keeps them
/// as the ui-screenshots artifact) and failed when it scrolls sideways; Dark chosen in the switch over a light
/// system; and the focus ring on the first top-bar link a keyboard reaches. Nothing compares the images: fonts
/// render differently from one machine to the next, so a screenshot is looked at, never diffed.
/// Opt-in (RUN_UI_TESTS=1), like <see cref="LoginFlowTests"/>: needs installed browsers and the app running.
/// </summary>
public sealed class DesignSystemProofTests(DesignSystemProofTests.ProofBrowser browser)
    : IClassFixture<DesignSystemProofTests.ProofBrowser>
{
    private const string Login = "login";
    private const string Register = "register";
    private const string InvoiceList = "invoices";
    private const string OverdueList = "invoices?status=Overdue";
    private const string NewInvoice = "invoices/new";
    private const string InvoiceDetail = "invoices/{id}";   // a draft the case creates through the New page

    private const string Light = "light";
    private const string Dark = "dark";

    private const int Phone = 400;
    private const int Laptop = 1280;
    private const int ViewportHeight = 900;

    private static readonly string[] Addresses = [Login, Register, InvoiceList, OverdueList, NewInvoice, InvoiceDetail];
    private static readonly int[] Widths = [Phone, Laptop];
    private static readonly string[] Schemes = [Light, Dark];

    /// <summary>Every page at every width in every scheme, each named for what it keeps and checks.</summary>
    public static IEnumerable<TheoryDataRow<string, int, string>> Cases()
    {
        foreach (string address in Addresses)
        {
            foreach (int width in Widths)
            {
                foreach (string scheme in Schemes)
                {
                    yield return new TheoryDataRow<string, int, string>(address, width, scheme)
                    {
                        TestDisplayName =
                            $"Design system proof, {Describe(address, width, scheme)}: kept as {FileName(address, width, scheme)}.png, no sideways scroll",
                    };
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Every_page_is_kept_at_both_widths_in_both_schemes_and_never_scrolls_sideways(
        string address, int width, string scheme)
    {
        Assert.SkipUnless(UiTestEnvironment.Enabled,
            "Browser UI test — set RUN_UI_TESTS=1 with browsers installed and the app running.");

        // The sign-in pages each get a page of their own; the protected ones share the class's signed-in page.
        bool signedOut = address is Login or Register;
        IPage page = signedOut ? await browser.NewPageAsync() : await browser.SignedInPageAsync();
        try
        {
            await page.SetViewportSizeAsync(width, ViewportHeight);
            await page.EmulateMediaAsync(new() { ColorScheme = scheme is Dark ? ColorScheme.Dark : ColorScheme.Light });

            await OpenAsync(page, address, customer: $"Design proof {width} {scheme}");

            await KeepAndCheckAsync(page, FileName(address, width, scheme), Describe(address, width, scheme));
        }
        finally
        {
            if (signedOut)
            {
                await page.Context.CloseAsync();
            }
        }
    }

    [Fact]
    public async Task Dark_chosen_in_the_switch_holds_over_a_light_system_on_the_invoice_list()
    {
        Assert.SkipUnless(UiTestEnvironment.Enabled,
            "Browser UI test — set RUN_UI_TESTS=1 with browsers installed and the app running.");

        // A page of its own: the choice is kept in its browser storage, which the shared page must not see.
        IPage page = await browser.NewPageAsync();
        try
        {
            await page.SetViewportSizeAsync(Laptop, ViewportHeight);
            await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
            await Session.SignInAsAdminAsync(page);
            await WaitForTheListAsync(page, "All");

            await page.SelectOptionAsync("select.theme-select", Dark);
            await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", Dark, new() { Timeout = 15_000 });
            string colorScheme = await page.EvaluateAsync<string>("getComputedStyle(document.documentElement).colorScheme");
            colorScheme.ShouldBe(Dark, StringComparer.Ordinal,
                "data-theme=\"dark\" sets color-scheme: dark, which light-dark() follows over the system's light scheme");

            await KeepAndCheckAsync(page, $"{Slug(InvoiceList)}-{Laptop}-dark-switch",
                $"{InvoiceList} at {Laptop} px, Dark chosen in the switch over a light system");
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    [Fact]
    public async Task Tab_from_the_top_of_the_page_reaches_the_first_top_bar_link_with_its_focus_ring_showing()
    {
        Assert.SkipUnless(UiTestEnvironment.Enabled,
            "Browser UI test — set RUN_UI_TESTS=1 with browsers installed and the app running.");

        IPage page = await browser.NewPageAsync();
        try
        {
            await page.GotoAsync($"{UiTestEnvironment.WebBaseUrl}/{Login}");
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
            // Once the circuit renders, FocusOnNavigate (Routes.razor) puts focus on the h1, as after every
            // navigation, and a Tab would go on from there. A click on the top bar's top edge, where nothing
            // focusable sits, takes focus off the h1 and starts the keyboard's order at the top of the document.
            await Assertions.Expect(page.Locator("h1")).ToBeFocusedAsync(new() { Timeout = 15_000 });
            await page.Locator("header.topbar").ClickAsync(new() { Position = new() { X = 1, Y = 1 } });

            await page.Keyboard.PressAsync("Tab");

            await Assertions.Expect(page.Locator("header.topbar a").First).ToBeFocusedAsync();
            string outline = await page.EvaluateAsync<string>("getComputedStyle(document.activeElement).outlineStyle");
            outline.ShouldNotBe("none", StringComparer.Ordinal,
                "the first top-bar link a keyboard reaches shows the focus ring (:focus-visible in app.css)");
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    /// <summary>
    /// Brings the page to the address and waits until it shows its content, the way LoginFlowTests waits. The
    /// protected pages are reached through the app's links: a full page load would start a signed-out circuit
    /// (ADR-0007). <paramref name="customer"/> names the draft the invoice page's case creates.
    /// </summary>
    private static async Task OpenAsync(IPage page, string address, string customer)
    {
        switch (address)
        {
            case Login or Register:
                await page.GotoAsync($"{UiTestEnvironment.WebBaseUrl}/{address}");
                // Wait for the Blazor Server circuit to attach: the page is the interactive one from here.
                await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
                await page.Locator("h1").WaitForAsync(new() { Timeout = 15_000 });
                break;

            case InvoiceList:
                await ShowTheListAsync(page);
                break;

            case OverdueList:
                await ShowTheListAsync(page);
                await page.ClickAsync("a.filter:has-text('Overdue')");
                await page.WaitForURLAsync("**/invoices?status=Overdue", new() { Timeout = 15_000 });
                await WaitForTheListAsync(page, "Overdue");
                break;

            case NewInvoice:
                await page.ClickAsync("a.nav-link:has-text('New')");
                await page.WaitForURLAsync("**/invoices/new", new() { Timeout = 15_000 });
                await page.Locator("h1", new() { HasText = "New invoice" }).WaitForAsync(new() { Timeout = 15_000 });
                break;

            case InvoiceDetail:
                // Created the way LoginFlowTests creates a draft; the success path lands on its page.
                await page.ClickAsync("a.nav-link:has-text('New')");
                await page.WaitForURLAsync("**/invoices/new", new() { Timeout = 15_000 });
                await page.FillAsync("label:has-text('Customer name') input", customer);
                await page.FillAsync("label:has-text('Customer email') input", "proof@test.dev");
                await page.FillAsync("label:has-text('Currency') input", "USD");
                await page.ClickAsync("button[type='submit']");
                await page.WaitForURLAsync(
                    new Regex("/invoices/[0-9a-fA-F-]{36}$", RegexOptions.None, TimeSpan.FromSeconds(5)),
                    new() { Timeout = 15_000 });
                // Its h1, or the alert a failed load shows in its place: either is kept, and the alert fails the case.
                await page.Locator("h1", new() { HasText = customer })
                    .Or(page.Locator("p.error[role='alert']"))
                    .WaitForAsync(new() { Timeout = 15_000 });
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(address), address, "No case knows the way to this page.");
        }
    }

    /// <summary>The invoice list under All, through the top bar's link.</summary>
    private static async Task ShowTheListAsync(IPage page)
    {
        await page.ClickAsync("a.nav-link:has-text('Invoices')");
        await page.WaitForURLAsync("**/invoices", new() { Timeout = 15_000 });
        await WaitForTheListAsync(page, "All");
    }

    /// <summary>The list shows the filter as current and has finished loading (no "Loading…" status).</summary>
    private static async Task WaitForTheListAsync(IPage page, string filter)
    {
        await Assertions.Expect(page.Locator("a.filter[aria-current='page']")).ToHaveTextAsync(filter, new() { Timeout = 15_000 });
        await Assertions.Expect(page.Locator("p[role='status']")).ToHaveCountAsync(0, new() { Timeout = 15_000 });
    }

    /// <summary>
    /// Saves the full page as <paramref name="name"/>.png in <see cref="UiTestEnvironment.ScreenshotRoot"/>,
    /// then fails when the page shows an error instead of its content or is wider than the window. The
    /// screenshot comes first, so a failing page is in the artifact too.
    /// </summary>
    private static async Task KeepAndCheckAsync(IPage page, string name, string what)
    {
        Directory.CreateDirectory(UiTestEnvironment.ScreenshotRoot);
        string file = Path.Combine(UiTestEnvironment.ScreenshotRoot, $"{name}.png");
        await page.ScreenshotAsync(new() { Path = file, FullPage = true });

        int alerts = await page.Locator("p.error[role='alert']").CountAsync();
        alerts.ShouldBe(0, $"{what} shows an error instead of its content (see {file})");
        bool fits = await page.EvaluateAsync<bool>(
            "document.documentElement.scrollWidth <= document.documentElement.clientWidth");
        fits.ShouldBeTrue($"{what} scrolls sideways: something on it is wider than the window (see {file})");
    }

    private static string Describe(string address, int width, string scheme) => $"{address} at {width} px in {scheme}";

    private static string FileName(string address, int width, string scheme) => $"{Slug(address)}-{width}-{scheme}";

    private static string Slug(string address) => address switch
    {
        OverdueList => "invoices-overdue",
        NewInvoice => "invoices-new",
        InvoiceDetail => "invoice-detail",
        _ => address,
    };

    /// <summary>
    /// One Chromium for the class and one page signed in as the seeded admin, which the protected pages' cases
    /// share: the API allows ten sign-ins a minute from one address and every browser test signs in from the web
    /// host's, so a sign-in per case would be refused. Both are made on first use, after a case's opt-in guard,
    /// so a run without browsers starts nothing; a failed sign-in is kept, and every later case fails with it
    /// instead of signing in again. The cases of one class run one at a time, taking turns on the shared page.
    /// </summary>
    public sealed class ProofBrowser : IAsyncDisposable
    {
        private IPlaywright? _playwright;
        private Task<IBrowser>? _browser;
        private Task<IPage>? _signedIn;

        /// <summary>A fresh page in a browser context of its own (its own storage, signed out); close its context.</summary>
        public async Task<IPage> NewPageAsync()
        {
            IBrowser chromium = await (_browser ??= LaunchAsync());
            return await chromium.NewPageAsync(new() { IgnoreHTTPSErrors = true });
        }

        /// <summary>The shared signed-in page, with no notification an earlier case raised still showing.</summary>
        public async Task<IPage> SignedInPageAsync()
        {
            IPage page = await (_signedIn ??= SignInAsync());
            // A notification shows for 8 s (the draft's "created", say), on whatever page comes next.
            await Assertions.Expect(page.Locator(".notifications .callout")).ToHaveCountAsync(0, new() { Timeout = 15_000 });
            return page;
        }

        public async ValueTask DisposeAsync()
        {
            if (_browser is { IsCompletedSuccessfully: true })
            {
                IBrowser chromium = await _browser;
                await chromium.DisposeAsync();   // closes every context and page it opened
            }

            _playwright?.Dispose();
        }

        private async Task<IBrowser> LaunchAsync()
        {
            _playwright = await Playwright.CreateAsync();
            return await _playwright.Chromium.LaunchAsync(new() { Headless = true });
        }

        private async Task<IPage> SignInAsync()
        {
            IPage page = await NewPageAsync();
            await Session.SignInAsAdminAsync(page);
            return page;
        }
    }
}
