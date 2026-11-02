#if (IncludeUI)
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Shouldly;
using Trellis.UI.Common.Layout;
using Xunit;

namespace Trellis.ApiTests.Ui;

/// <summary>
/// The theme switch as the server renders it, before any browser is attached: a labelled select on
/// System that has not touched JavaScript. Rendered by the framework's <see cref="HtmlRenderer"/>, which
/// never runs <c>OnAfterRenderAsync</c>, so the switch's read of the kept choice and its write on a change
/// are out of reach here and are not faked: the browser flow in Trellis.UiTests (<c>ThemeFlowTests</c>)
/// owns that path.
/// </summary>
public sealed partial class ThemeToggleTests
{
    [Fact]
    public async Task The_switch_renders_a_labelled_select_on_System_without_calling_the_browser()
    {
        var js = new PrerenderingJsRuntime();
        await using PageHost host = await PageHost.OpenAsync(
            signedIn: false, configureServices: services => services.AddSingleton<IJSRuntime>(js));

        string html = await host.RenderAsync<ThemeToggle>();

        html.ShouldContain("""<label class="theme"><span class="visually-hidden">Theme</span>""");
        html.ShouldContain("""<select class="theme-select" aria-label="Colour theme">""");
        Options(html).ShouldBe([("system", true, "System"), ("light", false, "Light"), ("dark", false, "Dark")]);
        js.Identifiers.ShouldBeEmpty("a render calls no JavaScript: while prerendering there is no browser and the call throws");
    }

    private static (string Value, bool Selected, string Text)[] Options(string html)
        => [.. Option().Matches(html).Select(static m => (m.Groups["value"].Value, m.Groups["selected"].Success, m.Groups["text"].Value))];

    [GeneratedRegex("""<option value="(?<value>[^"]*)"(?<selected> selected)?>(?<text>[^<]*)</option>""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Option();

    /// <summary>JavaScript as a prerender sees it: every call is recorded, then refused.</summary>
    private sealed class PrerenderingJsRuntime : IJSRuntime
    {
        private readonly List<string> _identifiers = [];

        public IReadOnlyList<string> Identifiers => _identifiers;

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => Refuse<TValue>(identifier);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => Refuse<TValue>(identifier);

        private ValueTask<TValue> Refuse<TValue>(string identifier)
        {
            _identifiers.Add(identifier);
            throw new InvalidOperationException("JavaScript interop calls cannot be issued at this time: the component is being statically rendered.");
        }
    }
}
#endif
