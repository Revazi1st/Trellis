#if (IncludeUI)
extern alias webhost;

using System.Net;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// UI.Web as it runs outside Development, where <c>UseExceptionHandler("/Error")</c> sends every unhandled
/// failure: the error page exists and renders statically (no circuit to fail again), pages carry the
/// circuit-failure bar and the theme switch with its script, and a closing circuit revokes its session.
/// No API behind it: these pages make no call.
/// </summary>
public sealed class WebHostTests(WebHostTests.ProductionWebHost host) : IClassFixture<WebHostTests.ProductionWebHost>
{
    private const string ThemeScript = "<script src=\"_content/Trellis.UI.Common/theme.js\"></script>";

    [Fact]
    public async Task Error_page_renders_statically_in_production()
    {
        using HttpClient client = host.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/Error", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.ShouldHaveMediaType("text/html");
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        html.ShouldContain("Something went wrong");
        html.ShouldContain("role=\"alert\"");
        // Interactive components leave Blazor markers in the prerendered HTML; the error page has none.
        html.ShouldNotContain("<!--Blazor:");
    }

    [Fact]
    public async Task Pages_render_interactively_and_carry_the_circuit_failure_bar()
    {
        using HttpClient client = host.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/login", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        html.ShouldContain("id=\"blazor-error-ui\"");
        html.ShouldContain("<!--Blazor:");
        // Production: the seeded-admin hint stays off.
        html.ShouldNotContain("Admin123!");
        // The theme switch: its script in the head ahead of the stylesheets (so the kept theme is set before
        // the first paint), and its select in the top bar, signed out too.
        int script = html.IndexOf(ThemeScript, StringComparison.Ordinal);
        script.ShouldBeGreaterThanOrEqualTo(0, $"the head loads the theme script: {ThemeScript}");
        script.ShouldBeLessThan(html.IndexOf("</head>", StringComparison.Ordinal));
        script.ShouldBeLessThan(html.IndexOf("_content/Trellis.UI.Common/tokens.css", StringComparison.Ordinal));
        html.ShouldContain("<select class=\"theme-select\"");
    }

    [Fact]
    public async Task The_theme_script_is_served_from_the_shared_UI()
    {
        using HttpClient client = host.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(
            new Uri("/_content/Trellis.UI.Common/theme.js", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.ShouldHaveMediaType("text/javascript");
        string script = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        script.ShouldContain("window.appTheme");
    }

    [Fact]
    public async Task A_closing_circuit_revokes_its_session()
    {
        await using AsyncServiceScope circuit = host.Services.CreateAsyncScope();

        CircuitHandler revoke = circuit.ServiceProvider.GetServices<CircuitHandler>()
            .Where(static handler => handler.GetType().Name == "RevokeSessionOnCircuitClose")
            .ShouldHaveSingleItem();

        // No session in this circuit: closing it completes without calling anything.
        await revoke.OnCircuitClosedAsync(circuit: null!, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// UI.Web booted as in production (the factory's default is Development). A published app serves the
    /// shared UI's static files from its own wwwroot; this one runs from the build output, whose files stay
    /// in their projects behind a manifest that only Development reads unless asked, so it is asked here.
    /// </summary>
    public sealed class ProductionWebHost : WebApplicationFactory<webhost::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
            => builder.UseEnvironment("Production").UseStaticWebAssets();
    }
}
#endif
