using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Trellis.ArchitectureTests;

/// <summary>
/// The license ceilings (ADR-0011) are written in three places that must name the same package ids: the list
/// the build enforces (<c>LicenseCeilingPackageIds</c> in <c>Directory.Build.props</c>), the LICENSE CEILINGS
/// comment in <c>Directory.Packages.props</c> that gives each id its reason, and Dependabot's ignore list. An id
/// in the list and nowhere else is a ban nobody can read the reason for; one in the comment and not the list is
/// a ban the build never enforces; one missing from Dependabot is an update the bot proposes anyway.
/// </summary>
public sealed partial class LicenseCeilingTests
{
    /// <summary>The comment's line that ends the ceilings the build enforces: what follows is a version ceiling it cannot.</summary>
    private const string VersionOnlyMarker = "A version ceiling the build cannot enforce";

    [Fact]
    public void The_license_ceilings_name_the_same_ids_in_the_build_the_comment_and_Dependabot()
    {
        string root = RepositorySource.Root.FullName;
        var sources = new CeilingSources(
            File.ReadAllText(Path.Combine(root, "Directory.Build.props")),
            File.ReadAllText(Path.Combine(root, "Directory.Packages.props")),
            File.ReadAllText(Path.Combine(root, ".github", "dependabot.yml")));

        EnforcedIds(sources.BuildProps).ShouldNotBeEmpty("no LicenseCeilingPackageIds in Directory.Build.props; is the repository root right?");
        sources.PackagesProps.ShouldContain(VersionOnlyMarker, Case.Sensitive,
            "the LICENSE CEILINGS comment no longer has the line that ends the enforced ceilings; keep the line or change the test with it");
        Mismatches(sources).ShouldBeEmpty(
            "an id goes into LicenseCeilingPackageIds (Directory.Build.props), the LICENSE CEILINGS comment with its reason "
            + "(Directory.Packages.props) and Dependabot's ignore list (.github/dependabot.yml) in one change (.claude/rules/dependencies.md)");
    }

    /// <summary>
    /// Positive control for the ceilings check, which expects zero mismatches: three small sources that agree,
    /// and the same sources with one id left out of one of them. A version ceiling below the marker line is the
    /// comment's own and owes the build nothing.
    /// </summary>
    [Theory]
    [InlineData("", false)]
    [InlineData("build", true)]
    [InlineData("comment", true)]
    [InlineData("dependabot", true)]
    public void The_ceiling_check_flags_an_id_missing_from_any_of_the_three(string droppedFrom, bool flagged)
    {
        string buildProps = droppedFrom == "build"
            ? "<LicenseCeilingPackageIds>Alpha.Core</LicenseCeilingPackageIds>"
            : "<LicenseCeilingPackageIds>Alpha.Core;Beta</LicenseCeilingPackageIds>";
        string comment = "<!-- LICENSE CEILINGS\n"
            + "  • Alpha.Core >= 2.0.0 (Alpha Ltd, commercial) — something free\n"
            + (droppedFrom == "comment" ? string.Empty : "  • Beta, any version (Beta LLC, proprietary) — something else\n")
            + $"  {VersionOnlyMarker}, because the id stays allowed below it:\n"
            + "  • Gamma >= 3.0.0 (a fee agreement in the package)\n"
            + "-->";
        string dependabot = "    ignore:\n"
            + "      - dependency-name: \"Alpha.Core\"\n"
            + (droppedFrom == "dependabot" ? string.Empty : "      - dependency-name: \"Beta\"\n")
            + "      - dependency-name: \"Gamma.*\"\n"
            + "        update-types: [\"version-update:semver-major\"]\n";

        (Mismatches(new CeilingSources(buildProps, comment, dependabot)).Count > 0).ShouldBe(flagged);
    }

    private sealed record CeilingSources(string BuildProps, string PackagesProps, string Dependabot);

    private static List<string> Mismatches(CeilingSources sources)
    {
        HashSet<string> enforced = new(EnforcedIds(sources.BuildProps), StringComparer.Ordinal);
        string comment = CeilingsComment(sources.PackagesProps);
        HashSet<string> reasoned = new(ReasonedIds(comment), StringComparer.Ordinal);
        HashSet<string> ignored = new(IgnoredIds(sources.Dependabot), StringComparer.Ordinal);

        return
        [
            .. enforced.Where(id => !NamesId(comment, id)).Order(StringComparer.Ordinal)
                .Select(static id => $"{id}: in LicenseCeilingPackageIds, but the LICENSE CEILINGS comment gives no reason for it"),
            .. reasoned.Except(enforced, StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(static id => $"{id}: the LICENSE CEILINGS comment refuses it, but LicenseCeilingPackageIds does not, so the build lets it in"),
            .. enforced.Except(ignored, StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(static id => $"{id}: in LicenseCeilingPackageIds, but Dependabot's ignore list does not name it"),
            .. ignored.Except(enforced, StringComparer.Ordinal).Order(StringComparer.Ordinal)
                .Select(static id => $"{id}: Dependabot ignores every version of it, but LicenseCeilingPackageIds does not list it"),
        ];
    }

    private static IEnumerable<string> EnforcedIds(string buildProps)
        => EnforcedList().Matches(buildProps)
            .SelectMany(static match => match.Groups["ids"].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    /// <summary>The LICENSE CEILINGS comment, from its title to the end of the XML comment it opens.</summary>
    private static string CeilingsComment(string packagesProps)
    {
        int start = packagesProps.IndexOf("LICENSE CEILINGS", StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Empty;
        }

        int end = packagesProps.IndexOf("-->", start, StringComparison.Ordinal);
        return packagesProps[start..(end < 0 ? packagesProps.Length : end)];
    }

    /// <summary>
    /// The ids the comment's bullets refuse above <see cref="VersionOnlyMarker"/>: each bullet's names before its
    /// parenthesis, comma-separated, a version ceiling or "any version" after a name dropped.
    /// </summary>
    private static IEnumerable<string> ReasonedIds(string comment)
    {
        int marker = comment.IndexOf(VersionOnlyMarker, StringComparison.Ordinal);
        string enforcedPart = marker < 0 ? comment : comment[..marker];
        return Bullet().Matches(enforcedPart)
            .SelectMany(static match => match.Groups["names"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(static name => name.Split(' ', 2)[0])
            .Where(static name => PackageId().IsMatch(name));
    }

    /// <summary>Dependabot's whole-package ignores: a <c>dependency-name</c> entry with no <c>update-types</c> under it.</summary>
    private static IEnumerable<string> IgnoredIds(string dependabot)
        => WholeIgnore().Matches(dependabot.ReplaceLineEndings("\n")).Select(static match => match.Groups["id"].Value);

    private static bool NamesId(string comment, string id)
        => Regex.IsMatch(comment, $@"(?<![\w.]){Regex.Escape(id)}(?![\w.])", RegexOptions.None, TimeSpan.FromSeconds(1));

    [GeneratedRegex(@"<LicenseCeilingPackageIds>(?<ids>[^<]*)</LicenseCeilingPackageIds>", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EnforcedList();

    [GeneratedRegex(@"^[ \t]*•[ \t]*(?<names>[^(\n]+?)[ \t]*\(", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]*(?:\.[A-Z][A-Za-z0-9]*)*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PackageId();

    [GeneratedRegex(@"^[ \t]*-[ \t]*dependency-name:[ \t]*""(?<id>[^""*]+)""[ \t]*\n(?![ \t]*update-types:)", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WholeIgnore();
}
