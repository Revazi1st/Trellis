#if (IncludeUI)
using System.Net;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Trellis.UI.Common;
using Trellis.UI.Common.Services;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// One page rendered in one scope (one circuit) at an address, signed in or not, over the real
/// <c>AddSharedUi</c> graph and a scripted API: the framework's <see cref="HtmlRenderer"/>, no browser, no
/// circuit. <see cref="Services"/> is the scope a page resolves from, so a test reaches what the page
/// reached (the auth state provider, a probe it registered) and <see cref="Navigation"/> where it was sent.
/// </summary>
internal sealed class PageHost : IAsyncDisposable
{
    private readonly ServiceProvider _root;
    private readonly AsyncServiceScope _scope;
    private readonly HtmlRenderer _renderer;

    private PageHost(string address, Action<IServiceCollection>? configureServices)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureHttpClientDefaults(http => http.ConfigurePrimaryHttpMessageHandler(() => Api));
        services.AddScoped<NavigationManager>(_ => new RecordingNavigationManager(address));
        services.AddScoped<ITokenStore, InMemoryTokenStore>();
        services.AddSharedUi(static (_, client) => client.BaseAddress = new Uri("http://api.test/"));
        // What a Blazor host's AddRazorComponents registers: [SupplyParameterFromQuery] reads the address.
        services.AddSupplyValueFromQueryProvider();
        configureServices?.Invoke(services);

        _root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _scope = _root.CreateAsyncScope();
        _renderer = new HtmlRenderer(_scope.ServiceProvider, _root.GetRequiredService<ILoggerFactory>());
    }

    public ScriptedApi Api { get; } = new();

    /// <summary>The page's scope: what it injects is what a test reads here.</summary>
    public IServiceProvider Services => _scope.ServiceProvider;

    public RecordingNavigationManager Navigation => (RecordingNavigationManager)Services.GetRequiredService<NavigationManager>();

    /// <summary>
    /// A host at <paramref name="address"/> (relative to the app's base), whose API answers every request
    /// with <paramref name="respond"/> (204 when none is given), signed in with <paramref name="permissions"/>
    /// unless <paramref name="signedIn"/> is false; <paramref name="configureServices"/> adds what a test's
    /// page needs beside the shared UI.
    /// </summary>
    public static async Task<PageHost> OpenAsync(
        string address = "",
        Func<HttpRequestMessage, HttpResponseMessage>? respond = null,
        IReadOnlyList<string>? permissions = null,
        bool signedIn = true,
        Action<IServiceCollection>? configureServices = null)
    {
        var host = new PageHost(address, configureServices);
        host.Api.Always(respond ?? (static _ => Responses.Status(HttpStatusCode.NoContent)));
        if (signedIn)
        {
            await host.Services.GetRequiredService<ITokenStore>()
                .SetTokensAsync(TestJwt.ExpiringAt(DateTimeOffset.UtcNow.AddHours(1), permissions: permissions), "refresh-1");
        }

        return host;
    }

    /// <summary>Renders <typeparamref name="TPage"/> and waits for its loads, then returns the HTML.</summary>
    public Task<string> RenderAsync<TPage>(IDictionary<string, object?>? parameters = null)
        where TPage : IComponent
        => _renderer.Dispatcher.InvokeAsync(async () =>
        {
            HtmlRootComponent root = await _renderer.RenderComponentAsync<TPage>(
                parameters is null ? ParameterView.Empty : ParameterView.FromDictionary(parameters));
            await root.QuiescenceTask;
            return root.ToHtmlString();
        });

    public async ValueTask DisposeAsync()
    {
        await _renderer.DisposeAsync();
        await _scope.DisposeAsync();
        await _root.DisposeAsync();
        Api.Dispose();
    }
}

/// <summary>Starts at an address and records where a page sent the user instead of going there.</summary>
internal sealed class RecordingNavigationManager : NavigationManager
{
    private readonly List<string> _navigations = [];
    private readonly TaskCompletionSource<string> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public RecordingNavigationManager(string address) => Initialize("http://ui.test/", "http://ui.test/" + address);

    public IReadOnlyList<string> Navigations => _navigations;

    /// <summary>The first navigation, waiting up to five seconds for it.</summary>
    public Task<string> NextAsync() => _first.Task.WaitAsync(TimeSpan.FromSeconds(5));

    protected override void NavigateToCore(string uri, NavigationOptions options)
    {
        _navigations.Add(uri);
        _first.TrySetResult(uri);
    }
}
#endif
