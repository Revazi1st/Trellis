#if (IncludeUI)
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Trellis.ArchitectureTests;

/// <summary>
/// The design system of the UI (docs/design-system.md, ADR-0032) held to its rules, by source scan over
/// the Razor and CSS files under <c>src/Presentation/UI</c>, the token file and the design doc:
/// <list type="bullet">
/// <item>only <c>tokens.css</c> writes a colour; a page carries classes, never a style attribute or block;</item>
/// <item>every colour token is one <c>light-dark(#light, #dark)</c> pair, and the <c>data-theme</c> blocks
/// set nothing but <c>color-scheme</c>;</item>
/// <item>text passes WCAG AA on the grounds it sits on, and every <c>--on-x</c> on its fill, in both schemes;</item>
/// <item>every class a page uses has a rule, every rule has its subsection in the doc, and the doc names
/// no class the stylesheet lacks;</item>
/// <item>every <c>var(--x)</c> a stylesheet reads is declared;</item>
/// <item>the doc's token tables and the token file name the same tokens.</item>
/// </list>
/// Each fact has a positive control below it: a scan that passes a clean codebase and a broken check
/// alike proves nothing. The UI folder is found by shape, <c>src/Presentation/UI/*.UI.Common/wwwroot</c>,
/// so the tests read the same in the template and in a generated project.
/// </summary>
public sealed partial class DesignSystemTests
{
    private const string UiDirectory = "src/Presentation/UI";
    private const string TokenFileName = "tokens.css";
    private const string StylesheetName = "app.css";
    private const string Light = "light";
    private const string Dark = "dark";
    private const string TokensHeading = "## Tokens";
    private const string ComponentsHeading = "## Components";
    private const double TextFloor = 4.5;
    private const double RingFloor = 3.0;

    private static readonly string DesignDocument = Path.Combine("docs", "design-system.md");

    /// <summary>
    /// Files under the UI that may write a colour outside the token file, keyed by repository-relative
    /// path, each with its reason. Empty on purpose: a colour is a token so that both schemes and the
    /// contrast facts see it. Adding an entry is a design-system decision: say why in the value.
    /// </summary>
    private static readonly Dictionary<string, string> LiteralColourAllowlist = new(StringComparer.Ordinal);

    private static readonly string[] Schemes = [Light, Dark];

    /// <summary>Text tokens that must pass <see cref="TextFloor"/> on <c>--bg</c> and <c>--surface</c>.</summary>
    private static readonly string[] TextTokens = ["ink", "muted", "accent", "danger", "success", "warning"];

    /// <summary>The text tokens that are also fills, so each has an <c>--on-x</c> for the text on it.</summary>
    private static readonly string[] FillTokens = ["accent", "danger", "success", "warning"];

    private static readonly string[] Grounds = ["bg", "surface"];

    private static readonly Lazy<DirectoryInfo> WwwRoot = new(FindWwwRoot);

    private static readonly Lazy<List<SourceFile>> UiFiles = new(() => [.. RepositorySource.RazorAndCssFiles(UiDirectory)]);

    private static readonly Lazy<SourceFile> TokenFile = new(() => FileUnderWwwRoot(TokenFileName));

    private static readonly Lazy<SourceFile> Stylesheet = new(() => FileUnderWwwRoot(StylesheetName));

    private static readonly Lazy<string[]> DocumentLines = new(() => File.ReadAllLines(Path.Combine(RepositorySource.Root.FullName, DesignDocument)));

    private static readonly Lazy<TokenSheet> Tokens = new(() => TokenSheet.Parse(TokenFile.Value.Code));

    [Fact]
    public void Only_the_token_file_writes_a_colour()
    {
        List<SourceFile> files = [.. UiFiles.Value.Where(static file => !ReferenceEquals(file, TokenFile.Value))];
        files.ShouldNotBeEmpty($"the scan found no .razor or .css file under {UiDirectory}; is the repository root right?");

        List<string> violations = [.. files
            .Where(static file => !LiteralColourAllowlist.ContainsKey(file.RelativePath))
            .SelectMany(static file => LiteralColoursIn(file.Code, IsCss(file.RelativePath))
                .Select(hit => $"{file.RelativePath}:{file.LineOf(hit.Index)}: {hit.Text}"))];

        violations.ShouldBeEmpty(
            $"write the colour as a token pair in {TokenFileName} and read it with var(--…); a page carries classes, never a style attribute "
            + "or a <style> block (docs/design-system.md). A deliberate exception goes on LiteralColourAllowlist with its reason");
    }

    /// <summary>
    /// Positive control for the colour scan, which expects zero hits: a hex, a colour function or a named
    /// colour in a stylesheet, and a style attribute or block in a page, are found; a token, a tint of two
    /// tokens, an id selector, a CSS escape and an HTML entity are not.
    /// </summary>
    [Theory]
    [InlineData(".a { color: #fff; }", ".css", true)]
    [InlineData(".a { color: #4f46e5; }", ".css", true)]
    [InlineData(".a { background: rgb(1 2 3); }", ".css", true)]
    [InlineData(".a { color: oklch(60% 0.1 150); }", ".css", true)]
    [InlineData(".a { color: white; }", ".css", true)]
    [InlineData(".a { border-color: RebeccaPurple; }", ".css", true)]
    [InlineData(".a { color: var(--ink); }", ".css", false)]
    [InlineData(".a { border-color: var(--danger); }", ".css", false)]
    [InlineData("#blazor-error-ui { display: none; }", ".css", false)]
    [InlineData(".back::before { content: \"\\2190\\00a0\"; }", ".css", false)]
    [InlineData(".a { background: color-mix(in srgb, var(--accent) 12%, var(--surface)); }", ".css", false)]
    [InlineData("<p style=\"color: red\">x</p>", ".razor", true)]
    [InlineData("<style>.a { }</style>", ".razor", true)]
    [InlineData("<span class=\"swatch\">#4f46e5</span>", ".razor", true)]
    [InlineData("<p class=\"error\" role=\"alert\">@_error</p>", ".razor", false)]
    [InlineData("<a href=\"#main\">Skip</a>", ".razor", false)]
    [InlineData("<span>&#8592; back</span>", ".razor", false)]
    [InlineData("<p>The ink is white on the dark fill.</p>", ".razor", false)]
    public void The_scan_flags_a_literal_colour_and_lets_a_token_through(string source, string extension, bool flagged)
        => LiteralColoursIn(source, IsCss(extension)).Any().ShouldBe(flagged);

    [Fact]
    public void Every_colour_token_is_written_once_for_both_schemes()
    {
        TokenSheet sheet = Tokens.Value;

        sheet.Declarations.ShouldNotBeEmpty($"no --token declared under :root in {TokenFileName}");
        sheet.Colours.ShouldNotBeEmpty($"no light-dark() colour token found in {TokenFileName}; has the shape changed?");
        sheet.Problems.ShouldBeEmpty(
            "every colour in tokens.css is one `--name: light-dark(#rrggbb, #rrggbb);` under :root, declared once; "
            + ":root sets `color-scheme: light dark`, and the two [data-theme] blocks set nothing but color-scheme");
    }

    /// <summary>
    /// Positive control for the token-shape check: the pair in six-digit hex passes; a shorthand, a single
    /// value, a var() or a third value, and any other colour notation, are refused; a token with no colour
    /// in it (a length, a border) is not a colour token and passes.
    /// </summary>
    [Theory]
    [InlineData("light-dark(#ffffff, #000000)", true)]
    [InlineData("light-dark(#FFFFFF,#000000)", true)]
    [InlineData("light-dark(#fff, #000)", false)]
    [InlineData("#ffffff", false)]
    [InlineData("light-dark(#ffffff, var(--y))", false)]
    [InlineData("light-dark(#ffffff, #000000, #111111)", false)]
    [InlineData("rgb(0 0 0)", false)]
    [InlineData("light-dark(white, black)", false)]
    [InlineData("1px solid var(--line)", true)]
    [InlineData("0.875rem", true)]
    public void The_scan_flags_a_colour_token_written_any_other_way(string value, bool accepted)
        => TokenSheet.IsWellFormedValue(value).ShouldBe(accepted);

    [Fact]
    public void The_scan_flags_a_token_declared_twice_a_theme_block_that_sets_a_value_and_a_media_block()
    {
        TokenSheet sheet = TokenSheet.Parse(
            ":root { color-scheme: light dark; --bg: light-dark(#ffffff, #000000); --bg: light-dark(#fafafa, #000000); }\n"
            + ":root[data-theme=\"light\"] { color-scheme: light; --bg: light-dark(#ffffff, #000000); }\n"
            + "@media (prefers-color-scheme: dark) { :root { --ink: light-dark(#000000, #ffffff); } }");

        sheet.Problems.ShouldContain(static p => p.Contains("--bg is declared 2 times", StringComparison.Ordinal));
        sheet.Problems.ShouldContain(static p => p.Contains("[data-theme=\"light\"] sets --bg", StringComparison.Ordinal));
        sheet.Problems.ShouldContain(static p => p.StartsWith("@media", StringComparison.Ordinal));
        sheet.Problems.ShouldContain(static p => p.Contains("[data-theme=\"dark\"]", StringComparison.Ordinal));
        sheet.Declarations.ShouldNotContain(static d => string.Equals(d.Name, "ink", StringComparison.Ordinal), "a nested block's declarations are not root tokens");
    }

    [Fact]
    public void Text_passes_AA_on_its_grounds_and_every_on_token_on_its_fill()
    {
        List<string> violations = ContrastViolations(Tokens.Value.Colours);

        violations.ShouldBeEmpty(
            $"move the value in {TokenFileName} until the pair passes (WCAG AA: {Floor(TextFloor)}:1 for text, {Floor(RingFloor)}:1 for the accent as a ring or a border); "
            + "the rule stays. A light accent used as text on a light ground is the usual failure");
    }

    /// <summary>
    /// Positive control for the contrast fact: a lime accent on a light ground (the dark scheme's accent
    /// written into the light one) is reported by name, scheme and ratio, and a missing token is reported
    /// rather than skipped.
    /// </summary>
    [Fact]
    public void The_contrast_check_flags_a_pair_below_its_floor_and_a_missing_token()
    {
        Dictionary<string, ColourPair> planted = new(Tokens.Value.Colours, StringComparer.Ordinal)
        {
            ["accent"] = new("#8fd16a", "#8fd16a"),
        };
        planted.Remove("on-warning");

        List<string> violations = ContrastViolations(planted);

        violations.ShouldContain(static v => v.StartsWith("--accent on --surface (light): 1.83", StringComparison.Ordinal));
        violations.ShouldContain(static v => v.StartsWith("--accent on --bg (light): 1.7", StringComparison.Ordinal));
        violations.ShouldContain(static v => v.StartsWith("--on-accent on --accent (light): 1.83", StringComparison.Ordinal));
        violations.ShouldContain(static v => v.StartsWith("--on-warning is missing", StringComparison.Ordinal));
        violations.ShouldNotContain(static v => v.Contains("(dark)", StringComparison.Ordinal), "the dark scheme was left as shipped");
    }

    [Theory]
    [InlineData("#000000", "#ffffff", 21.0)]
    [InlineData("#ffffff", "#ffffff", 1.0)]
    [InlineData("#777777", "#ffffff", 4.48)]
    [InlineData("#ffffff", "#4f46e5", 6.29)]
    [InlineData("#0e2415", "#8fd16a", 8.97)]
    public void The_contrast_arithmetic_is_WCAG_2(string a, string b, double expected)
        => Wcag.Contrast(a, b).ShouldBe(expected, 0.005);

    [Fact]
    public void Every_class_a_page_uses_has_a_rule_and_every_rule_is_documented()
    {
        List<(SourceFile File, int Index, string Class)> used = [.. UiFiles.Value
            .Where(static file => !IsCss(file.RelativePath))
            .SelectMany(static file => StaticClassesIn(file.Code).Select(hit => (file, hit.Index, hit.Text)))];
        HashSet<string> styled = new(ClassesSelectedIn(Stylesheet.Value.Code), StringComparer.Ordinal);
        List<(int Line, string Class)> documented = [.. SectionLines(ComponentsHeading)
            .SelectMany(static line => ClassesNamedIn(line.Text).Select(name => (line.Number, name)))];

        used.ShouldNotBeEmpty("the scan found no class attribute in any .razor file; has the markup changed?");
        styled.ShouldNotBeEmpty($"the scan found no class selector in {StylesheetName}");
        documented.ShouldNotBeEmpty($"no `.class` named under '{ComponentsHeading}' in {DesignDocument}");

        HashSet<string> documentedNames = new(documented.Select(static d => d.Class), StringComparer.Ordinal);
        List<string> violations =
        [
            .. used.Where(u => !styled.Contains(u.Class))
                .Select(u => $"{u.File.RelativePath}:{u.File.LineOf(u.Index)}: class \"{u.Class}\" has no rule in {StylesheetName}"),
            .. styled.Where(s => !documentedNames.Contains(s)).Order(StringComparer.Ordinal)
                .Select(s => $"{StylesheetName} styles .{s}, which has no subsection under '{ComponentsHeading}' in {DesignDocument}"),
            .. documented.Where(d => !styled.Contains(d.Class))
                .Select(d => $"{DesignDocument}:{d.Line}: names .{d.Class}, which {StylesheetName} does not style"),
        ];

        violations.ShouldBeEmpty(
            "a page is built from the documented components: a new class is a component with its rule in app.css and its "
            + "subsection in the doc's Components section (`.name` in backticks), and a dropped rule leaves the doc with it");
    }

    /// <summary>Positive controls for the class scan's three readers: the page side, the stylesheet side and the doc side.</summary>
    [Theory]
    [InlineData("<a href=\"x\" class=\"filter\">All</a>", "filter")]
    [InlineData("<div class=\"filters label\">", "filters,label")]
    [InlineData("<td class=\"@css\">", "")]
    [InlineData("<td class=\"@(right ? \"num\" : null)\">", "")]
    [InlineData("<NavLink href=\"invoices\" class=\"back\" Match=\"NavLinkMatch.All\">Back</NavLink>", "back")]
    public void The_page_reader_takes_static_class_values_only(string razor, string expected)
        => string.Join(',', StaticClassesIn(razor).Select(static hit => hit.Text)).ShouldBe(expected);

    [Theory]
    [InlineData(".grid th, .grid td { color: var(--x); }", "grid")]
    [InlineData(".filter[aria-current] { }", "filter")]
    [InlineData("input[aria-invalid=\"true\"] { }", "")]
    [InlineData("#blazor-error-ui .reload { }", "reload")]
    [InlineData(".nav-link.active { }", "nav-link,active")]
    [InlineData(".a { margin: .5rem; }", "a")]
    [InlineData(".a { margin: .5rem } .b { }", "a,b")]
    [InlineData("@media (min-width: 50rem) { .b { } }", "b")]
    public void The_stylesheet_reader_takes_classes_from_selectors_only(string css, string expected)
        => string.Join(',', ClassesSelectedIn(css).Distinct(StringComparer.Ordinal)).ShouldBe(expected);

    [Theory]
    [InlineData("`.filter[aria-current]` is the current one", "filter")]
    [InlineData("`.nav-link.active` is underlined", "nav-link,active")]
    [InlineData("`#blazor-error-ui .reload` reloads", "reload")]
    [InlineData("linked from `tokens.css` and `app.css`", "")]
    [InlineData("`input[aria-invalid=\"true\"]` turns the hairline red", "")]
    [InlineData("```html", "")]
    public void The_doc_reader_takes_classes_from_selector_spans_only(string line, string expected)
        => string.Join(',', ClassesNamedIn(line)).ShouldBe(expected);

    [Fact]
    public void Every_variable_a_stylesheet_reads_is_declared()
    {
        HashSet<string> declared = new(Tokens.Value.Declarations.Select(static d => d.Name), StringComparer.Ordinal);
        declared.UnionWith(DeclaredVariablesIn(Stylesheet.Value.Code));

        List<string> violations = [.. UndeclaredVariablesIn(Stylesheet.Value, declared).Concat(UndeclaredVariablesIn(TokenFile.Value, declared))];

        violations.ShouldBeEmpty($"a var(--x) nothing declares falls back to nothing in the browser; declare the token in {TokenFileName} or the alias in the rule");
    }

    [Fact]
    public void The_scan_flags_a_variable_nothing_declares()
    {
        var sheet = new SourceFile("app.css", ".a { --tone: var(--danger); color: var(--inkk); border: var(--tone); }");
        HashSet<string> declared = new(["danger", "ink"], StringComparer.Ordinal);
        declared.UnionWith(DeclaredVariablesIn(sheet.Code));

        UndeclaredVariablesIn(sheet, declared).ShouldBe(["app.css:1: var(--inkk)"]);
    }

    [Fact]
    public void The_design_doc_and_the_token_file_name_the_same_tokens()
    {
        HashSet<string> inFile = new(Tokens.Value.Declarations.Select(static d => d.Name), StringComparer.Ordinal);
        HashSet<string> inTables = new(SectionLines(TokensHeading).SelectMany(static line => TokenNamesIn(line.Text)), StringComparer.Ordinal);
        List<(int Line, string Name)> anywhere = [.. DocumentLines.Value
            .Select(static (text, index) => (Number: index + 1, Text: text))
            .SelectMany(static line => TokenNamesIn(line.Text).Select(name => (line.Number, name)))];

        inTables.ShouldNotBeEmpty($"no `--token` found under '{TokensHeading}' in {DesignDocument}");

        List<string> violations =
        [
            .. NamesOnOneSideOnly(inTables, inFile),
            .. anywhere.Where(a => !inFile.Contains(a.Name)).Select(a => $"{DesignDocument}:{a.Line}: --{a.Name} is not declared in {TokenFileName}"),
        ];

        violations.ShouldBeEmpty($"the Tokens tables of {DesignDocument} and {TokenFileName} name the same tokens: a token is added, renamed or removed in both");
    }

    [Theory]
    [InlineData("| `--bg` | the page | `body` |", "bg")]
    [InlineData("`--on-accent` and `--space-2`", "on-accent,space-2")]
    [InlineData("a token pair with its `--on-*`", "")]
    [InlineData("`var(--ink)` reads it", "")]
    public void The_doc_reader_takes_whole_token_spans_only(string line, string expected)
        => string.Join(',', TokenNamesIn(line)).ShouldBe(expected);

    [Fact]
    public void The_scan_flags_a_token_named_on_one_side_only()
        => NamesOnOneSideOnly(new HashSet<string>(["bg", "fg"], StringComparer.Ordinal), new HashSet<string>(["bg", "ink"], StringComparer.Ordinal))
            .ShouldBe([$"--fg is in the Tokens tables of {DesignDocument} but not in {TokenFileName}", $"--ink is in {TokenFileName} but not in the Tokens tables of {DesignDocument}"]);

    private static DirectoryInfo FindWwwRoot()
    {
        var ui = new DirectoryInfo(Path.Combine(RepositorySource.Root.FullName, UiDirectory));
        List<DirectoryInfo> candidates = ui.Exists
            ? [.. ui.EnumerateDirectories("*.UI.Common").Select(static d => new DirectoryInfo(Path.Combine(d.FullName, "wwwroot"))).Where(static d => d.Exists)]
            : [];

        return candidates.Count == 1
            ? candidates[0]
            : throw new InvalidOperationException($"expected one {UiDirectory}/*.UI.Common/wwwroot, found {candidates.Count}");
    }

    private static SourceFile FileUnderWwwRoot(string name)
    {
        string relative = Path.GetRelativePath(RepositorySource.Root.FullName, Path.Combine(WwwRoot.Value.FullName, name)).Replace('\\', '/');
        return UiFiles.Value.SingleOrDefault(file => string.Equals(file.RelativePath, relative, StringComparison.Ordinal))
               ?? throw new InvalidOperationException($"{relative} is missing");
    }

    private static bool IsCss(string path) => path.EndsWith(".css", StringComparison.OrdinalIgnoreCase);

    private static string Floor(double floor) => floor.ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Every literal colour, and in a page every style attribute or block, with where it starts.</summary>
    private static IEnumerable<Hit> LiteralColoursIn(string code, bool css)
    {
        IEnumerable<Hit> hits = HexColour().Matches(code).Concat(ColourFunction().Matches(code)).Select(static m => new Hit(m.Index, m.Value));
        hits = css
            ? hits.Concat(DeclarationValue().Matches(code)
                .Select(static m => (Value: m.Groups["value"], Name: NamedColour().Match(m.Groups["value"].Value)))
                .Where(static v => v.Name.Success)
                .Select(static v => new Hit(v.Value.Index + v.Name.Index, v.Name.Value)))
            : hits.Concat(InlineStyle().Matches(code).Concat(StyleElement().Matches(code)).Select(static m => new Hit(m.Index, m.Value)));

        return hits.OrderBy(static hit => hit.Index);
    }

    /// <summary>
    /// The contrast pairs the design doc requires, each below its floor reported by token, ground, scheme and
    /// ratio; a token the pairs need and the file lacks is reported once, by name.
    /// </summary>
    private static List<string> ContrastViolations(IReadOnlyDictionary<string, ColourPair> colours)
    {
        List<string> violations = [.. TextTokens.Concat(Grounds).Append("surface-2").Concat(FillTokens.Select(static fill => "on-" + fill))
            .Where(name => !colours.ContainsKey(name))
            .Select(name => $"--{name} is missing: the design doc names it, and every fill has the token for the text on it")];

        foreach (string scheme in Schemes)
        {
            foreach (string text in TextTokens)
            {
                foreach (string ground in Grounds)
                {
                    Check(text, ground, TextFloor);
                }
            }

            Check("ink", "surface-2", TextFloor);
            Check("accent", "bg", RingFloor);
            Check("accent", "surface", RingFloor);

            // The fills a project adds are checked by name too: every --on-x against its --x.
            foreach (string fill in colours.Keys.Where(static name => name.StartsWith("on-", StringComparison.Ordinal)).Select(static name => name[3..]).Order(StringComparer.Ordinal))
            {
                Check("on-" + fill, fill, TextFloor);
            }

            void Check(string text, string ground, double floor)
            {
                if (!colours.TryGetValue(text, out ColourPair? textPair) || !colours.TryGetValue(ground, out ColourPair? groundPair))
                {
                    return;   // reported above, once
                }

                double ratio = Wcag.Contrast(textPair.Of(scheme), groundPair.Of(scheme));
                if (ratio < floor)
                {
                    violations.Add(string.Create(CultureInfo.InvariantCulture,
                        $"--{text} on --{ground} ({scheme}): {ratio:0.00}, the floor is {floor:0.0} ({textPair.Of(scheme)} on {groundPair.Of(scheme)})"));
                }
            }
        }

        return violations;
    }

    /// <summary>The static class names a page writes: every <c>class="a b"</c> whose value holds no Razor expression.</summary>
    private static IEnumerable<Hit> StaticClassesIn(string razor)
        => StaticClassAttribute().Matches(razor)
            .SelectMany(static m => m.Groups["classes"].Value
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => new Hit(m.Groups["classes"].Index, name)));

    /// <summary>The class names a stylesheet selects: every <c>.name</c> in the text before a <c>{</c>, never inside a declaration.</summary>
    private static IEnumerable<string> ClassesSelectedIn(string css)
        => SelectorPrelude().Matches(css)
            .SelectMany(static m => SelectorClass().Matches(m.Groups["prelude"].Value).Select(static c => c.Groups["name"].Value));

    /// <summary>
    /// The class names a doc line names in a selector span: a code span that starts with <c>.</c> or <c>#</c>,
    /// or holds a descendant <c> .</c>, yields each of its classes; <c>`tokens.css`</c> is a file, not a class.
    /// </summary>
    private static IEnumerable<string> ClassesNamedIn(string line)
        => CodeSpan().Matches(line)
            .Select(static m => m.Groups["code"].Value)
            .Where(static span => span.StartsWith('.') || span.StartsWith('#') || span.Contains(" .", StringComparison.Ordinal))
            .SelectMany(static span => SelectorClass().Matches(span).Select(static c => c.Groups["name"].Value));

    /// <summary>The token names a doc line names as whole code spans: <c>`--bg`</c>, never <c>`var(--bg)`</c> or <c>`--on-*`</c>.</summary>
    private static IEnumerable<string> TokenNamesIn(string line)
        => CodeSpan().Matches(line)
            .Select(static m => m.Groups["code"].Value)
            .Where(static span => WholeToken().IsMatch(span))
            .Select(static span => span[2..]);

    private static IEnumerable<string> DeclaredVariablesIn(string css)
        => VariableDeclaration().Matches(css).Select(static m => m.Groups["name"].Value);

    private static IEnumerable<string> UndeclaredVariablesIn(SourceFile sheet, HashSet<string> declared)
        => VariableRead().Matches(sheet.Code)
            .Where(m => !declared.Contains(m.Groups["name"].Value))
            .Select(m => $"{sheet.RelativePath}:{sheet.LineOf(m.Index)}: {m.Value})");

    private static IEnumerable<string> NamesOnOneSideOnly(HashSet<string> inTables, HashSet<string> inFile)
        => inTables.Where(name => !inFile.Contains(name)).Order(StringComparer.Ordinal)
            .Select(name => $"--{name} is in the Tokens tables of {DesignDocument} but not in {TokenFileName}")
            .Concat(inFile.Where(name => !inTables.Contains(name)).Order(StringComparer.Ordinal)
                .Select(name => $"--{name} is in {TokenFileName} but not in the Tokens tables of {DesignDocument}"));

    /// <summary>The lines of one level-two section of the design doc, up to the next level-two heading, with their 1-based numbers.</summary>
    private static IEnumerable<(int Number, string Text)> SectionLines(string heading)
    {
        string[] lines = DocumentLines.Value;
        int start = Array.FindIndex(lines, line => string.Equals(line.Trim(), heading, StringComparison.Ordinal));
        if (start < 0)
        {
            return [];
        }

        return lines.Select(static (text, index) => (Number: index + 1, Text: text))
            .Skip(start + 1)
            .TakeWhile(static line => !SectionEnd().IsMatch(line.Text));
    }

    /// <summary>A hex colour; an HTML numeric entity (<c>&amp;#8592;</c>) is not one.</summary>
    [GeneratedRegex(@"(?<!&)#(?:[0-9a-fA-F]{8}|[0-9a-fA-F]{6}|[0-9a-fA-F]{3,4})\b", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex HexColour();

    [GeneratedRegex(@"\b(?:rgba?|hsla?|hwb|lab|lch|oklab|oklch|color)\s*\(", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ColourFunction();

    /// <summary>A declaration's value: what follows a colon up to the next semicolon or brace.</summary>
    [GeneratedRegex(@":(?<value>[^;{}]*)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DeclarationValue();

    /// <summary>The CSS named colours (transparent and currentcolor are keywords, not colours, and stay allowed).</summary>
    [GeneratedRegex(
        @"\b(?:aliceblue|antiquewhite|aqua|aquamarine|azure|beige|bisque|black|blanchedalmond|blue|blueviolet|brown|burlywood|cadetblue"
        + @"|chartreuse|chocolate|coral|cornflowerblue|cornsilk|crimson|cyan|darkblue|darkcyan|darkgoldenrod|darkgray|darkgreen|darkgrey"
        + @"|darkkhaki|darkmagenta|darkolivegreen|darkorange|darkorchid|darkred|darksalmon|darkseagreen|darkslateblue|darkslategray"
        + @"|darkslategrey|darkturquoise|darkviolet|deeppink|deepskyblue|dimgray|dimgrey|dodgerblue|firebrick|floralwhite|forestgreen"
        + @"|fuchsia|gainsboro|ghostwhite|gold|goldenrod|gray|green|greenyellow|grey|honeydew|hotpink|indianred|indigo|ivory|khaki"
        + @"|lavender|lavenderblush|lawngreen|lemonchiffon|lightblue|lightcoral|lightcyan|lightgoldenrodyellow|lightgray|lightgreen"
        + @"|lightgrey|lightpink|lightsalmon|lightseagreen|lightskyblue|lightslategray|lightslategrey|lightsteelblue|lightyellow|lime"
        + @"|limegreen|linen|magenta|maroon|mediumaquamarine|mediumblue|mediumorchid|mediumpurple|mediumseagreen|mediumslateblue"
        + @"|mediumspringgreen|mediumturquoise|mediumvioletred|midnightblue|mintcream|mistyrose|moccasin|navajowhite|navy|oldlace|olive"
        + @"|olivedrab|orange|orangered|orchid|palegoldenrod|palegreen|paleturquoise|palevioletred|papayawhip|peachpuff|peru|pink|plum"
        + @"|powderblue|purple|rebeccapurple|red|rosybrown|royalblue|saddlebrown|salmon|sandybrown|seagreen|seashell|sienna|silver|skyblue"
        + @"|slateblue|slategray|slategrey|snow|springgreen|steelblue|tan|teal|thistle|tomato|turquoise|violet|wheat|white|whitesmoke"
        + @"|yellow|yellowgreen)\b",
        RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NamedColour();

    [GeneratedRegex(@"\bstyle\s*=", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InlineStyle();

    [GeneratedRegex(@"<style\b", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StyleElement();

    /// <summary>A class attribute whose value is static: no <c>@</c> in it.</summary>
    [GeneratedRegex(@"\bclass\s*=\s*""(?<classes>[^""@]*)""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex StaticClassAttribute();

    /// <summary>The text before a <c>{</c>: a rule's selectors or an at-rule's prelude, never a declaration.</summary>
    [GeneratedRegex(@"(?<prelude>[^{};]+)\{", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SelectorPrelude();

    [GeneratedRegex(@"\.(?<name>[a-zA-Z_][\w-]*)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SelectorClass();

    [GeneratedRegex(@"var\(\s*--(?<name>[a-zA-Z_][\w-]*)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VariableRead();

    [GeneratedRegex(@"--(?<name>[a-zA-Z_][\w-]*)\s*:", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VariableDeclaration();

    [GeneratedRegex("`(?<code>[^`]+)`", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"^--[a-z][a-z0-9-]*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WholeToken();

    /// <summary>The next level-two heading ends a section.</summary>
    [GeneratedRegex(@"^##\s", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SectionEnd();

    private sealed record Hit(int Index, string Text);

    /// <summary>A colour token's two values.</summary>
    private sealed record ColourPair(string LightValue, string DarkValue)
    {
        public string Of(string scheme) => string.Equals(scheme, Light, StringComparison.Ordinal) ? LightValue : DarkValue;
    }

    private sealed record Declaration(string Name, string Value);

    /// <summary>The token file read: its declarations, the colour pairs among them, and every departure from the shape.</summary>
    private sealed partial class TokenSheet
    {
        public List<Declaration> Declarations { get; } = [];

        public Dictionary<string, ColourPair> Colours { get; } = new(StringComparer.Ordinal);

        public List<string> Problems { get; } = [];

        public static TokenSheet Parse(string css)
        {
            var sheet = new TokenSheet();
            bool rootSetsScheme = false;
            HashSet<string> themeBlocks = new(StringComparer.Ordinal);

            foreach ((string prelude, string body) in Blocks(css))
            {
                Match theme = ThemeSelector().Match(prelude);
                if (body.Contains('{', StringComparison.Ordinal))
                {
                    sheet.Problems.Add($"{prelude}: a block inside a block; the token file is flat, every value under :root");
                }
                else if (string.Equals(prelude, ":root", StringComparison.Ordinal))
                {
                    rootSetsScheme |= ColourScheme().Matches(body).Any(static m => string.Equals(m.Groups["value"].Value.Trim(), "light dark", StringComparison.Ordinal));
                    foreach (Match declaration in TokenDeclaration().Matches(body))
                    {
                        string name = declaration.Groups["name"].Value;
                        string value = declaration.Groups["value"].Value.Trim();
                        sheet.Declarations.Add(new Declaration(name, value));

                        Match pair = LightDarkPair().Match(value);
                        if (pair.Success)
                        {
                            sheet.Colours[name] = new ColourPair("#" + pair.Groups[Light].Value.ToLowerInvariant(), "#" + pair.Groups[Dark].Value.ToLowerInvariant());
                        }
                        else if (!IsWellFormedValue(value))
                        {
                            sheet.Problems.Add($"--{name}: {value} is a colour written any other way than light-dark(#rrggbb, #rrggbb)");
                        }
                    }
                }
                else if (theme.Success)
                {
                    themeBlocks.Add(theme.Groups["scheme"].Value);
                    foreach (Match declaration in AnyDeclaration().Matches(body)
                                 .Where(static m => !string.Equals(m.Groups["property"].Value, "color-scheme", StringComparison.Ordinal)))
                    {
                        sheet.Problems.Add($"{prelude} sets {declaration.Groups["property"].Value}: a theme block sets only color-scheme; the values are light-dark() pairs under :root");
                    }
                }
                else
                {
                    sheet.Problems.Add($"{prelude}: a scheme is a light-dark() pair under :root, not another block");
                }
            }

            if (!rootSetsScheme)
            {
                sheet.Problems.Add(":root does not set `color-scheme: light dark`, so light-dark() has nothing to follow");
            }

            foreach (string scheme in Schemes.Where(scheme => !themeBlocks.Contains(scheme)))
            {
                sheet.Problems.Add($":root[data-theme=\"{scheme}\"] {{ color-scheme: {scheme}; }} is missing, so an attribute on <html> cannot hold that theme");
            }

            foreach (IGrouping<string, Declaration> twice in sheet.Declarations.GroupBy(static d => d.Name, StringComparer.Ordinal).Where(static g => g.Count() > 1))
            {
                sheet.Problems.Add(string.Create(CultureInfo.InvariantCulture, $"--{twice.Key} is declared {twice.Count()} times; a token is written once"));
            }

            return sheet;
        }

        /// <summary>A value with no colour in it, or exactly one <c>light-dark(#rrggbb, #rrggbb)</c>.</summary>
        public static bool IsWellFormedValue(string value)
            => LightDarkPair().IsMatch(value)
               || !(value.Contains('#', StringComparison.Ordinal)
                    || value.Contains("light-dark", StringComparison.Ordinal)
                    || ColourFunction().IsMatch(value)
                    || NamedColour().IsMatch(value));

        /// <summary>
        /// The top-level blocks of a stylesheet as (prelude, body), the body kept whole when it nests another
        /// block, so the caller can refuse it. A brace scan rather than a regex: a regex over <c>[^{}]</c> would
        /// read a block nested in an at-rule as a top-level one.
        /// </summary>
        private static IEnumerable<(string Prelude, string Body)> Blocks(string css)
        {
            var prelude = new StringBuilder();
            var body = new StringBuilder();
            int depth = 0;
            foreach (char c in css)
            {
                if (c == '{')
                {
                    if (depth > 0)
                    {
                        body.Append(c);
                    }

                    depth++;
                }
                else if (c == '}')
                {
                    depth = Math.Max(depth - 1, 0);
                    if (depth > 0)
                    {
                        body.Append(c);
                    }
                    else
                    {
                        yield return (prelude.ToString().Trim(), body.ToString());
                        prelude.Clear();
                        body.Clear();
                    }
                }
                else if (depth == 0)
                {
                    prelude.Append(c);
                }
                else
                {
                    body.Append(c);
                }
            }
        }

        [GeneratedRegex(@"^:root\[data-theme=""(?<scheme>light|dark)""\]$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
        private static partial Regex ThemeSelector();

        [GeneratedRegex(@"--(?<name>[a-zA-Z_][\w-]*)\s*:\s*(?<value>[^;]+);", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
        private static partial Regex TokenDeclaration();

        [GeneratedRegex(@"(?<property>[a-zA-Z-]+)\s*:\s*(?<value>[^;]+);", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
        private static partial Regex AnyDeclaration();

        [GeneratedRegex(@"\bcolor-scheme\s*:\s*(?<value>[^;]+);", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
        private static partial Regex ColourScheme();

        [GeneratedRegex(@"^light-dark\(\s*#(?<light>[0-9a-fA-F]{6})\s*,\s*#(?<dark>[0-9a-fA-F]{6})\s*\)$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
        private static partial Regex LightDarkPair();
    }

    /// <summary>WCAG 2 relative luminance and contrast ratio (https://www.w3.org/TR/WCAG21/#dfn-relative-luminance).</summary>
    private static class Wcag
    {
        public static double Contrast(string a, string b)
        {
            double la = Luminance(a);
            double lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        private static double Luminance(string colour)
        {
            (int r, int g, int b) = Channels(colour);
            return (0.2126 * Linear(r)) + (0.7152 * Linear(g)) + (0.0722 * Linear(b));
        }

        private static double Linear(int channel)
        {
            double c = channel / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        private static (int R, int G, int B) Channels(string colour)
            => (Channel(colour, 1), Channel(colour, 3), Channel(colour, 5));

        private static int Channel(string colour, int start)
            => int.Parse(colour.AsSpan(start, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    }
}
#endif
