#if (IncludeUI)
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.UI.Common.Auth;
using Xunit;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// <see cref="ProtectedPage"/> guards when it opens and again whenever the auth state changes while it
/// is open (a sign-out, a session ended by a rejected refresh): an anonymous user goes to sign-in with
/// this page as the <c>returnUrl</c>, a user without the permission goes to the invoice list. Rendered by
/// the framework's <see cref="HtmlRenderer"/> over the real <c>AddSharedUi</c> graph: no browser, no circuit.
/// </summary>
public sealed class ProtectedPageTests
{
    private const string Here = "invoices/42";

    [Fact]
    public async Task An_anonymous_user_is_sent_to_sign_in_with_this_page_to_return_to()
    {
        await using PageHost page = await OpenAsync<SignedInPage>(signedIn: false);

        (await page.Navigation.NextAsync()).ShouldBe("login?returnUrl=invoices%2F42");
        ProbeOf(page).Authorized.ShouldBeFalse();
    }

    [Fact]
    public async Task A_sign_out_while_the_page_is_open_sends_the_user_to_sign_in()
    {
        await using PageHost page = await OpenAsync<SignedInPage>(signedIn: true);
        ProbeOf(page).Authorized.ShouldBeTrue();
        page.Navigation.Navigations.ShouldBeEmpty();

        await page.Services.GetRequiredService<TokenAuthenticationStateProvider>().SignOutAsync(TestContext.Current.CancellationToken);

        (await page.Navigation.NextAsync()).ShouldBe("login?returnUrl=invoices%2F42");
    }

    [Fact]
    public async Task A_user_without_the_permission_is_sent_to_the_invoice_list()
    {
        await using PageHost page = await OpenAsync<WriterOnlyPage>(signedIn: true);

        (await page.Navigation.NextAsync()).ShouldBe("invoices");
        ProbeOf(page).Authorized.ShouldBeFalse();
    }

    /// <summary>The page opened at <see cref="Here"/> in a <see cref="PageHost"/> that registers its probe, rendered once.</summary>
    private static async Task<PageHost> OpenAsync<TPage>(bool signedIn)
        where TPage : IComponent
    {
        PageHost host = await PageHost.OpenAsync(Here, signedIn: signedIn, configureServices: static services => services.AddScoped<PageProbe>());
        _ = await host.RenderAsync<TPage>();   // the guard's navigation and the probe are what these tests read, not the markup
        return host;
    }

    private static PageProbe ProbeOf(PageHost page) => page.Services.GetRequiredService<PageProbe>();

    /// <summary>What the page under test did.</summary>
    private sealed class PageProbe
    {
        public bool Authorized { get; set; }
    }

    /// <summary>A page any signed-in user may see.</summary>
    private class SignedInPage : ProtectedPage
    {
        [Inject]
        private PageProbe Probe { get; set; } = default!;

        protected override Task OnAuthorizedAsync()
        {
            Probe.Authorized = true;
            return Task.CompletedTask;
        }
    }

    private sealed class WriterOnlyPage : SignedInPage
    {
        protected override string RequiredPermission => "invoices:write";
    }
}
#endif
