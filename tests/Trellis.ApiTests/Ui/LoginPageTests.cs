#if (IncludeUI)
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Trellis.UI.Common;
using Trellis.UI.Common.Pages;
using Trellis.UI.Common.Services;
using Xunit;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// The sign-in page advertises the seeded admin's well-known credentials only where the host says it is
/// a developer's machine (<see cref="UiOptions.ShowDevelopmentHints"/>). Rendered to HTML with the
/// framework's static <see cref="HtmlRenderer"/>: no browser, no circuit.
/// </summary>
public sealed class LoginPageTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Seeded_admin_hint_renders_only_when_the_host_enables_development_hints(bool showDevelopmentHints)
    {
        string html = await RenderLoginAsync(options => options.ShowDevelopmentHints = showDevelopmentHints);

        html.Contains("Admin123!", StringComparison.Ordinal).ShouldBe(showDevelopmentHints);
        html.ShouldContain("Create one");
    }

    [Fact]
    public async Task Seeded_admin_hint_is_off_when_the_host_says_nothing()
    {
        string html = await RenderLoginAsync(configure: null);

        html.ShouldNotContain("Admin123!");
    }

    private static async Task<string> RenderLoginAsync(Action<UiOptions>? configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<NavigationManager, StaticNavigationManager>();
        services.AddScoped<ITokenStore, InMemoryTokenStore>();
        services.AddSharedUi(static (_, client) => client.BaseAddress = new Uri("http://api.test/"));
        if (configure is not null)
        {
            services.Configure(configure);
        }

        await using ServiceProvider root = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using AsyncServiceScope scope = root.CreateAsyncScope();
        await using var renderer = new HtmlRenderer(scope.ServiceProvider, root.GetRequiredService<ILoggerFactory>());

        return await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<Login>()).ToHtmlString());
    }

    private sealed class StaticNavigationManager : NavigationManager
    {
        public StaticNavigationManager() => Initialize("http://ui.test/", "http://ui.test/login");
    }
}
#endif
