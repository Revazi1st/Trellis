using System.Text.Json;
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

    private const string TemplateConfig = ".template.config/template.json";

    /// <summary>
    /// The template engine replaces every occurrence of a <c>port</c> symbol's literal (<c>template.json</c>, the
    /// AppHost's launch-profile ports such as <c>17171</c>) in every file it processes, not only in
    /// <c>launchSettings.json</c>. A migration id is a timestamp and can contain one by chance
    /// (<c>20261111171711</c> holds <c>17171</c>): the generated project then carries a <c>[Migration]</c> attribute
    /// rewritten to a port number under a file still named after the real id, and its own
    /// <c>migrations remove</c> finds no file to remove. Such an id is refused here: regenerate at another minute.
    /// </summary>
    [Fact]
    public void No_migration_id_contains_a_literal_the_template_engine_rewrites()
    {
        string root = RepositorySource.Root.FullName;
        string[] literals = PortLiterals(File.ReadAllText(Path.Combine(root, TemplateConfig)));
        string[] ids = [.. Directory.EnumerateFiles(Path.Combine(root, MigrationsFolder), "*.cs")
            .Select(static path => Path.GetFileName(path))
            .Where(static name => !name.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))
            .Select(static name => name[..name.IndexOf('_', StringComparison.Ordinal)])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        literals.ShouldNotBeEmpty($"no port symbol with a 'replaces' literal in {TemplateConfig}; did the AppHost ports move?");
        ids.ShouldNotBeEmpty($"no migration under {MigrationsFolder}");
        RewrittenIds(ids, literals).ShouldBeEmpty(
            "a migration id contains a literal the template engine replaces in every generated project (a port symbol's "
            + $"'replaces' value in {TemplateConfig}), so the generated [Migration] attribute no longer matches its file: "
            + "regenerate the migration at another minute (env -u TRELLIS_DESIGNTIME_DB ./scripts/ef.sh migrations remove "
            + "--force ..., then migrations add ...)");
    }

    /// <summary>
    /// Positive control: an id that contains a port literal anywhere is flagged with the literal it holds, a clean id
    /// is not, and the literal reader takes only <c>port</c> symbols' <c>replaces</c> values.
    /// </summary>
    [Theory]
    [InlineData("20261111171711", "17171")]
    [InlineData("20261011131954", null)]
    [InlineData("20261109030823", null)]
    [InlineData("20261015171100", "15171")]
    public void The_literal_check_flags_an_id_that_contains_a_port_literal(string id, string? literal)
    {
        const string config = """
            { "symbols": {
              "name": { "type": "parameter", "replaces": "Trellis" },
              "appHostHttpPort": { "type": "generated", "generator": "port", "parameters": { "fallback": 15171 }, "replaces": "15171" },
              "appHostHttpsPort": { "type": "generated", "generator": "port", "parameters": { "fallback": 17171 }, "replaces": "17171" },
              "templateVersion": { "type": "generated", "generator": "constant", "replaces": "TEMPLATE-MARKER-VERSION" } } }
            """;

        string[] literals = PortLiterals(config);

        literals.ShouldBe(["15171", "17171"]);
        RewrittenIds([id], literals).ShouldBe(literal is null ? [] : [$"{id} holds {literal}"]);
    }

    /// <summary>The <c>replaces</c> literal of every <c>port</c> symbol in the template's configuration.</summary>
    private static string[] PortLiterals(string templateJson)
    {
        using JsonDocument document = JsonDocument.Parse(templateJson);
        return [.. document.RootElement.GetProperty("symbols").EnumerateObject()
            .Where(static symbol => symbol.Value.TryGetProperty("generator", out JsonElement generator) && generator.ValueEquals("port"))
            .Select(static symbol => symbol.Value.GetProperty("replaces").GetString())
            .OfType<string>()
            .Order(StringComparer.Ordinal)];
    }

    /// <summary>Each id that contains one of <paramref name="literals"/>, with the literal it holds.</summary>
    private static string[] RewrittenIds(IEnumerable<string> ids, IEnumerable<string> literals)
        => [.. ids.SelectMany(id => literals.Where(literal => id.Contains(literal, StringComparison.Ordinal)).Select(literal => $"{id} holds {literal}"))];

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
