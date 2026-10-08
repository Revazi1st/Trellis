#if (IncludeUI)
using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Trellis.UI.Common;
using Trellis.UI.Common.Auth;
using Trellis.UI.Common.Services;
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
        await using PageHost page = await PageHost.OpenAsync<SignedInPage>(signedIn: false);

        (await page.Navigation.NextAsync()).ShouldBe("login?returnUrl=invoices%2F42");
        page.Probe.Authorized.ShouldBeFalse();
    }

    [Fact]
    public async Task A_sign_out_while_the_page_is_open_sends_the_user_to_sign_in()
    {
        await using PageHost page = await PageHost.OpenAsync<SignedInPage>(signedIn: true);
        page.Probe.Authorized.ShouldBeTrue();
        page.Navigation.Navigations.ShouldBeEmpty();

        await page.AuthState.SignOutAsync(TestContext.Current.CancellationToken);

        (await page.Navigation.NextAsync()).ShouldBe("login?returnUrl=invoices%2F42");
    }

    [Fact]
    public async Task A_user_without_the_permission_is_sent_to_the_invoice_list()
    {
        await using PageHost page = await PageHost.OpenAsync<WriterOnlyPage>(signedIn: true);

        (await page.Navigation.NextAsync()).ShouldBe("invoices");
        page.Probe.Authorized.ShouldBeFalse();
    }

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

    /// <summary>One page rendered in one scope (one circuit), with its session and the navigation it asked for.</summary>
    private sealed class PageHost : IAsyncDisposable
    {
        private readonly ServiceProvider _root;
        private readonly AsyncServiceScope _scope;
        private readonly HtmlRenderer _renderer;
        private readonly ScriptedApi _api = new();

        private PageHost()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => _api));
            services.AddScoped<NavigationManager, RecordingNavigationManager>();
            services.AddScoped<ITokenStore, InMemoryTokenStore>();
            services.AddScoped<PageProbe>();
            services.AddSharedUi(static (_, client) => client.BaseAddress = new Uri("http://api.test/"));
            _api.Always(static _ => Responses.Status(HttpStatusCode.NoContent));

            _root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            _scope = _root.CreateAsyncScope();
            _renderer = new HtmlRenderer(_scope.ServiceProvider, _root.GetRequiredService<ILoggerFactory>());
        }

        public RecordingNavigationManager Navigation
            => (RecordingNavigationManager)_scope.ServiceProvider.GetRequiredService<NavigationManager>();

        public TokenAuthenticationStateProvider AuthState
            => _scope.ServiceProvider.GetRequiredService<TokenAuthenticationStateProvider>();

        public PageProbe Probe => _scope.ServiceProvider.GetRequiredService<PageProbe>();

        public static async Task<PageHost> OpenAsync<TPage>(bool signedIn)
            where TPage : IComponent
        {
            var host = new PageHost();
            if (signedIn)
            {
                await host._scope.ServiceProvider.GetRequiredService<ITokenStore>()
                    .SetTokensAsync(TestJwt.ExpiringAt(DateTimeOffset.UtcNow.AddHours(1)), "refresh-1");
            }

            await host._renderer.Dispatcher.InvokeAsync(async () =>
            {
                HtmlRootComponent root = await host._renderer.RenderComponentAsync<TPage>();
                await root.QuiescenceTask;
            });
            return host;
        }

        public async ValueTask DisposeAsync()
        {
            await _renderer.DisposeAsync();
            await _scope.DisposeAsync();
            await _root.DisposeAsync();
            _api.Dispose();
        }
    }

    /// <summary>Records where the page sent the user instead of going there.</summary>
    private sealed class RecordingNavigationManager : NavigationManager
    {
        private readonly List<string> _navigations = [];
        private readonly TaskCompletionSource<string> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public RecordingNavigationManager() => Initialize("http://ui.test/", "http://ui.test/" + Here);

        public IReadOnlyList<string> Navigations => _navigations;

        /// <summary>The first navigation, waiting up to five seconds for it.</summary>
        public Task<string> NextAsync() => _first.Task.WaitAsync(TimeSpan.FromSeconds(5));

        protected override void NavigateToCore(string uri, NavigationOptions options)
        {
            _navigations.Add(uri);
            _first.TrySetResult(uri);
        }
    }
}
#endif
