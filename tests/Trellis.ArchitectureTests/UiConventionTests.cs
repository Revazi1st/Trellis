#if (IncludeUI)
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Trellis.ArchitectureTests;

/// <summary>
/// Three UI rules (.claude/rules/ui.md) held by source scan over the <c>.razor</c> and <c>.cs</c> files under
/// <c>src/Presentation/UI</c>:
/// <list type="bullet">
/// <item>no endpoint <c>[Authorize]</c>: client-token Blazor Server has no server scheme to challenge
/// (ADR-0007), so a page inherits <c>ProtectedPage</c> and gates by permission with <c>AuthorizeView</c>;</item>
/// <item>only the typed clients talk to the API: nothing in the UI builds an <c>HttpClient</c> of its own, and
/// no page or layout POSTs, which would skip <c>ApiCall</c>'s failure mapping and the fresh
/// <c>Idempotency-Key</c> every logical POST carries;</item>
/// <item>no inline <c>&lt;script&gt;</c>: the host's Content-Security-Policy is <c>script-src 'self'</c>, so a
/// script lives in a file.</item>
/// </list>
/// Each fact has a positive control below it; the scans read <c>src/</c> only, so the controls' rows are
/// written out whole. A deliberate exception goes on the fact's allowlist, keyed by repository-relative
/// path, with its reason; an entry the scan no longer needs fails it.
/// </summary>
public sealed partial class UiConventionTests
{
    private const string UiDirectory = "src/Presentation/UI";

    /// <summary>UI files that may carry an endpoint <c>[Authorize]</c>. Empty on purpose: there is no scheme to challenge (ADR-0007).</summary>
    private static readonly Dictionary<string, string> EndpointAuthorizeAllowlist = new(StringComparer.Ordinal);

    /// <summary>UI files that may build an <c>HttpClient</c> or POST from a page or layout. Empty on purpose: the typed clients do both.</summary>
    private static readonly Dictionary<string, string> RawHttpAllowlist = new(StringComparer.Ordinal);

    /// <summary>Razor files that may carry an inline script. Empty on purpose: the Content-Security-Policy refuses one.</summary>
    private static readonly Dictionary<string, string> InlineScriptAllowlist = new(StringComparer.Ordinal);

    private static readonly Lazy<List<SourceFile>> UiFiles = new(() =>
    [
        .. RepositorySource.CSharpFiles(UiDirectory),
        .. RepositorySource.RazorAndCssFiles(UiDirectory).Where(static file => IsRazor(file.RelativePath)),
    ]);

    [Fact]
    public void Pages_are_guarded_in_the_component_never_by_an_endpoint_Authorize()
    {
        List<SourceFile> files = UiFiles.Value;

        files.ShouldNotBeEmpty($"the scan found no .razor or .cs file under {UiDirectory}; is the repository root right?");
        SourceScan.StaleEntries(files, EndpointAuthorizeAllowlist, EndpointAuthorizesIn).ShouldBeEmpty();
        SourceScan.Violations(files, EndpointAuthorizeAllowlist, EndpointAuthorizesIn).ShouldBeEmpty(
            "inherit ProtectedPage and override OnAuthorizedAsync; gate a part of a page with <AuthorizeView Roles=\"permission:…\"> "
            + "(ui.md, ADR-0007). An endpoint [Authorize] has no server scheme to challenge in client-token Blazor Server");
    }

    /// <summary>
    /// Positive control for the <c>[Authorize]</c> scan: the attribute is found on a page or a class, bare,
    /// with arguments or fully qualified; <c>AuthorizeView</c>, <c>AuthorizeRouteView</c> and
    /// <c>[AllowAnonymous]</c> are not.
    /// </summary>
    [Theory]
    [InlineData("@attribute [Authorize]", true)]
    [InlineData("@attribute [Authorize(Roles = \"permission:invoices.read\")]", true)]
    [InlineData("@attribute [Microsoft.AspNetCore.Authorization.Authorize]", true)]
    [InlineData("[Authorize] public sealed partial class Planted : ComponentBase;", true)]
    [InlineData("<AuthorizeView Roles=\"permission:invoices.write\">", false)]
    [InlineData("<AuthorizeRouteView RouteData=\"routeData\" />", false)]
    [InlineData("@attribute [AllowAnonymous]", false)]
    public void The_scan_flags_an_endpoint_Authorize_and_lets_AuthorizeView_through(string code, bool flagged)
        => EndpointAuthorizesIn(new SourceFile($"{UiDirectory}/Planted.razor", code)).Any().ShouldBe(flagged);

    [Fact]
    public void Only_the_typed_clients_talk_to_the_API()
    {
        List<SourceFile> files = UiFiles.Value;

        files.ShouldNotBeEmpty($"the scan found no .razor or .cs file under {UiDirectory}; is the repository root right?");
        SourceScan.StaleEntries(files, RawHttpAllowlist, RawHttpIn).ShouldBeEmpty();
        SourceScan.Violations(files, RawHttpAllowlist, RawHttpIn).ShouldBeEmpty(
            "call the API through a typed client (IInvoiceApiClient, IAuthApiClient) whose methods go through ApiCall; a new call is a "
            + "method there, which sends a fresh Idempotency-Key per logical POST (ui.md). AddSharedUi builds the clients' HttpClient");
    }

    /// <summary>
    /// Positive control for the raw-HTTP scan: an <c>HttpClient</c> built anywhere in the UI, and a POST from a
    /// page or a layout, are found; the typed client's own POST and a page calling the typed client are not.
    /// </summary>
    [Theory]
    [InlineData("Services/Planted.cs", "var client = new HttpClient();", true)]
    [InlineData("Services/Planted.cs", "using var client = new System.Net.Http.HttpClient(handler);", true)]
    [InlineData("Pages/Planted.razor", "await Http.PostAsJsonAsync(\"api/v1/invoices\", _model);", true)]
    [InlineData("Layout/Planted.razor", "await Http.PostAsync(\"api/v1/auth/logout\", content: null);", true)]
    [InlineData("Services/Planted.cs", "=> ApiCall.SendAsync(ct => httpClient.PostAsJsonAsync(\"api/v1/auth/logout\", request, ct), cancellationToken);", false)]
    [InlineData("Pages/Planted.razor", "ApiResult<bool> result = await OrderApi.PayAsync(Id, _order.Version);", false)]
    public void The_scan_flags_a_raw_HttpClient_and_a_POST_from_a_page(string path, string code, bool flagged)
        => RawHttpIn(new SourceFile($"{UiDirectory}/Trellis.UI.Common/{path}", code)).Any().ShouldBe(flagged);

    [Fact]
    public void Scripts_live_in_files_never_inline()
    {
        List<SourceFile> pages = [.. UiFiles.Value.Where(static file => IsRazor(file.RelativePath))];

        pages.ShouldNotBeEmpty($"the scan found no .razor file under {UiDirectory}; is the repository root right?");
        SourceScan.StaleEntries(pages, InlineScriptAllowlist, InlineScriptsIn).ShouldBeEmpty();
        SourceScan.Violations(pages, InlineScriptAllowlist, InlineScriptsIn).ShouldBeEmpty(
            "move the script to a file under wwwroot and load it with <script src=\"…\">: UI.Web's Content-Security-Policy is "
            + "script-src 'self', which refuses an inline script (ui.md)");
    }

    /// <summary>
    /// Positive control for the inline-script scan: a script element without a <c>src</c>, in any case, is
    /// found; one that loads a file, and <c>&lt;noscript&gt;</c>, are not.
    /// </summary>
    [Theory]
    [InlineData("<script>alert(1)</script>", true)]
    [InlineData("<script type=\"module\">import('./theme.js');</script>", true)]
    [InlineData("<SCRIPT>start()</SCRIPT>", true)]
    [InlineData("<script src=\"_framework/blazor.web.js\"></script>", false)]
    [InlineData("<script defer src=\"js/theme.js\"></script>", false)]
    [InlineData("<noscript>This page needs JavaScript.</noscript>", false)]
    public void The_scan_flags_an_inline_script_and_lets_a_script_file_through(string razor, bool flagged)
        => InlineScriptsIn(new SourceFile($"{UiDirectory}/Planted.razor", razor)).Any().ShouldBe(flagged);

    private static bool IsRazor(string path) => path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);

    /// <summary>A page or a layout: the files that render, which reach the API only through a typed client.</summary>
    private static bool IsPageOrLayout(string path)
        => path.Contains("/Pages/", StringComparison.Ordinal) || path.Contains("/Layout/", StringComparison.Ordinal);

    private static IEnumerable<int> EndpointAuthorizesIn(SourceFile file) => EndpointAuthorize().Matches(file.Code).Select(static m => m.Index);

    private static IEnumerable<int> RawHttpIn(SourceFile file)
        => NewHttpClient().Matches(file.Code)
            .Concat(IsPageOrLayout(file.RelativePath) ? PostCall().Matches(file.Code) : Enumerable.Empty<Match>())
            .Select(static m => m.Index)
            .Order();

    private static IEnumerable<int> InlineScriptsIn(SourceFile file) => InlineScript().Matches(file.Code).Select(static m => m.Index);

    /// <summary><c>[Authorize]</c> or <c>[AuthorizeAttribute]</c>, qualified or not; <c>AuthorizeView</c> is another word.</summary>
    [GeneratedRegex(@"\[\s*(?:global::)?(?:\w+\.)*Authorize(?:Attribute)?\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EndpointAuthorize();

    [GeneratedRegex(@"\bnew\s+(?:global::)?(?:System\.Net\.Http\.)?HttpClient\s*\(", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NewHttpClient();

    [GeneratedRegex(@"\.Post(?:AsJson)?Async\s*\(", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PostCall();

    /// <summary>A script element whose start tag carries no <c>src</c> attribute.</summary>
    [GeneratedRegex(@"<script\b(?![^>]*\bsrc\s*=)[^>]*>", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InlineScript();
}
#endif
