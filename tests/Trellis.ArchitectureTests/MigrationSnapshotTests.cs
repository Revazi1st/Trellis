using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Trellis.ArchitectureTests;

/// <summary>
/// The template ships its migrations written by the EF Core it pins: <c>InitialCreate</c> is regenerated on the
/// current EF before a release (<c>.claude/rules/persistence.md</c>, Migrations), so a generated project's first
/// migration rewrites no <c>ProductVersion</c> line and the model change is what its snapshot diff shows. A
/// Dependabot bump of <c>Microsoft.EntityFrameworkCore</c> fails here until the migration is regenerated.
/// The template repository's own fact: <c>template.json</c> and the pack leave it out of a generated project,
/// where a deployed database forbids regenerating <c>InitialCreate</c> and the next real migration rewrites the
/// line anyway.
/// </summary>
public sealed partial class MigrationSnapshotTests
{
    private const string MigrationsFolder = "src/Infrastructure/Trellis.Persistence/Migrations";

    private const string EfCorePackage = "Microsoft.EntityFrameworkCore";

    [Fact]
    public void The_migrations_were_written_by_the_pinned_EF_Core()
    {
        string root = RepositorySource.Root.FullName;
        string props = File.ReadAllText(Path.Combine(root, "Directory.Packages.props"));
        string[] written = [.. Directory.EnumerateFiles(Path.Combine(root, MigrationsFolder), "*.cs")
            .Where(static path => path.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal) || path.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

        written.ShouldNotBeEmpty($"no model snapshot or migration designer under {MigrationsFolder}; is the repository root right?");
        written.Select(path => VersionDrift(props, Path.GetFileName(path), File.ReadAllText(path))).OfType<string>().ShouldBeEmpty(
            "regenerate the initial migration on the pinned EF Core: env -u TRELLIS_DESIGNTIME_DB ./scripts/ef.sh "
            + "migrations remove --force --project src/Infrastructure/Trellis.Persistence, then ./scripts/ef.sh "
            + "migrations add InitialCreate --project src/Infrastructure/Trellis.Persistence (docs/development.md)");
    }

    /// <summary>
    /// Positive control: a snapshot written by another EF Core than the pin is flagged, so is one with no
    /// <c>ProductVersion</c> to read, and the reader takes the bare package's pin, not the version of a package
    /// whose id only starts with it (<c>.Design</c>, which may be listed first).
    /// </summary>
    [Theory]
    [InlineData("10.0.13", "10.0.13", false)]
    [InlineData("10.0.13", "10.0.12", true)]
    [InlineData("10.1.0", "10.0.13", true)]
    [InlineData("10.0.13", null, true)]
    public void The_version_check_flags_a_snapshot_written_by_another_EF_Core(string pinned, string? written, bool flagged)
    {
        string props = $"""
            <PackageVersion Include="{EfCorePackage}.Design" Version="9.9.9" />
            <PackageVersion Include="{EfCorePackage}" Version="{pinned}" />
            """;
        string snapshot = written is null
            ? "modelBuilder.HasAnnotation(\"Relational:MaxIdentifierLength\", 63);"
            : $"modelBuilder.HasAnnotation(\"ProductVersion\", \"{written}\").HasAnnotation(\"Relational:MaxIdentifierLength\", 63);";

        (VersionDrift(props, "AppDbContextModelSnapshot.cs", snapshot) is not null).ShouldBe(flagged);
    }

    /// <summary>What is wrong with <paramref name="file"/>'s <c>ProductVersion</c> against the pin, or null when it matches.</summary>
    private static string? VersionDrift(string props, string file, string code)
    {
        string? pinned = PinnedEfCore().Match(props) is { Success: true } pin ? pin.Groups["version"].Value : null;
        string? written = ProductVersion().Match(code) is { Success: true } annotation ? annotation.Groups["version"].Value : null;
        return pinned is not null && string.Equals(pinned, written, StringComparison.Ordinal)
            ? null
            : $"{file}: written by EF Core {written ?? "(no ProductVersion)"}, Directory.Packages.props pins {pinned ?? "(no pin)"}";
    }

    [GeneratedRegex("""<PackageVersion\s+Include="Microsoft\.EntityFrameworkCore"\s+Version="(?<version>[^"]+)"\s*/>""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PinnedEfCore();

    [GeneratedRegex("""\.HasAnnotation\(\s*"ProductVersion"\s*,\s*"(?<version>[^"]+)"\s*\)""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ProductVersion();
}
