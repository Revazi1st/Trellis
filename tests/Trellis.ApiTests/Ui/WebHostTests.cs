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
/// circuit-failure bar, and a closing circuit revokes its session. No API behind it: these pages make no call.
/// </summary>
public sealed class WebHostTests(WebHostTests.ProductionWebHost host) : IClassFixture<WebHostTests.ProductionWebHost>
{
    [Fact]
    public async Task Error_page_renders_statically_in_production()
    {
        using HttpClient client = host.CreateClient();

        using HttpResponseMessage response = await client.GetAsync(new Uri("/Error", UriKind.Relative), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("text/html");
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

    /// <summary>UI.Web booted as in production (the factory's default is Development).</summary>
    public sealed class ProductionWebHost : WebApplicationFactory<webhost::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.UseEnvironment("Production");
    }
}
#endif
