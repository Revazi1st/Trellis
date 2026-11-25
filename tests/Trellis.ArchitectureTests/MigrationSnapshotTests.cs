using System.Text.Json;
using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Trellis.ArchitectureTests;

/// <summary>
/// The template ships two initial migrations and a generated project gets one of them: the default set, in the
/// Persistence project, and the set for the multi-tenant model (<c>--use-multitenancy</c>), which sits outside
/// every project so that nothing here compiles it and dotnet-ef never finds it. This repository builds only the
/// default model, so these facts are what holds the second set here: both are written by the EF Core the
/// template pins (<c>.claude/rules/persistence.md</c>, Migrations; a Dependabot bump of
/// <c>Microsoft.EntityFrameworkCore</c> fails until both are regenerated), neither id holds a literal the
/// template engine rewrites, the two hold the same migrations and describe the same model but for the tenant
/// seam, and <c>template.json</c> ships exactly one of them. Template CI's multi-tenant legs then run the
/// real check, <c>MigrationDriftTests</c>, on the model itself, and its pack job runs
/// <c>scripts/regenerate-initial-migrations.sh --check</c>, which generates both sets and compares them with
/// these files.
/// The template repository's own facts: <c>template.json</c> and the pack leave them out of a generated project,
/// where a deployed database forbids regenerating <c>InitialCreate</c> and the next real migration rewrites the
/// version line anyway.
/// </summary>
public sealed partial class MigrationSnapshotTests
{
    private const string DefaultSet = "src/Infrastructure/Trellis.Persistence/Migrations";

    /// <summary>The initial migration of the multi-tenant model, which <c>template.json</c> ships in place of the default one.</summary>
    private const string TenancySet = ".template.config/tenancy/Migrations";

    private static readonly string[] Sets = [DefaultSet, TenancySet];

    private const string Snapshot = "AppDbContextModelSnapshot.cs";

    private const string EfCorePackage = "Microsoft.EntityFrameworkCore";

    /// <summary>The remedy every failure about the committed files ends in: one run regenerates both sets.</summary>
    private const string Regenerate = "regenerate both sets: scripts/regenerate-initial-migrations.sh";

    [Fact]
    public void The_migrations_were_written_by_the_pinned_EF_Core()
    {
        string props = File.ReadAllText(Path.Combine(RepositorySource.Root.FullName, "Directory.Packages.props"));

        foreach (string set in Sets)
        {
            string[] written = [.. FilesOf(set).Where(static path => path.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal) || path.EndsWith(".Designer.cs", StringComparison.Ordinal))];

            written.ShouldNotBeEmpty($"no model snapshot or migration designer under {set}; is the repository root right? If it is, {Regenerate}");
            written.Select(path => VersionDrift(props, $"{set}/{Path.GetFileName(path)}", File.ReadAllText(path))).OfType<string>().ShouldBeEmpty(
                $"written by another EF Core than the pinned one; {Regenerate}");
        }
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

        (VersionDrift(props, Snapshot, snapshot) is not null).ShouldBe(flagged);
    }

    private const string TemplateConfig = ".template.config/template.json";

    /// <summary>
    /// The template engine replaces every occurrence of a <c>port</c> symbol's literal (<c>template.json</c>, the
    /// AppHost's launch-profile ports such as <c>17171</c>) in every file it processes, not only in
    /// <c>launchSettings.json</c>. A migration id is a timestamp and can contain one by chance
    /// (<c>20261111171711</c> holds <c>17171</c>): the generated project then carries a <c>[Migration]</c> attribute
    /// rewritten to a port number under a file still named after the real id, and its own
    /// <c>migrations remove</c> finds no file to remove. Such an id is refused here, in either set; the
    /// regeneration script generates again a minute later until the id holds none (a literal sits in an id for
    /// ten minutes at most).
    /// </summary>
    [Fact]
    public void No_migration_id_contains_a_literal_the_template_engine_rewrites()
    {
        string[] literals = PortLiterals(File.ReadAllText(Path.Combine(RepositorySource.Root.FullName, TemplateConfig)));
        string[] ids = [.. Sets.SelectMany(MigrationFileNamesOf)
            .Select(static name => name[..name.IndexOf('_', StringComparison.Ordinal)])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        literals.ShouldNotBeEmpty($"no port symbol with a 'replaces' literal in {TemplateConfig}; did the AppHost ports move?");
        ids.ShouldNotBeEmpty($"no migration under {string.Join(" or ", Sets)}; {Regenerate}");
        RewrittenIds(ids, literals).ShouldBeEmpty(
            "a migration id contains a literal the template engine replaces in every generated project (a port symbol's "
            + $"'replaces' value in {TemplateConfig}), so the generated [Migration] attribute no longer matches its file "
            + $"(the script generates again a minute later until the id holds none); {Regenerate}");
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

    /// <summary>
    /// A project generated with <c>--use-multitenancy</c> gets the second set under the first one's folder, and
    /// the engine rewrites the template's name wherever it stands. So the second set holds the migrations the
    /// first holds, by name, and every file of both declares the Persistence project's migrations namespace: a
    /// set generated under another project's name would ship that name, in its namespace and in the CLR names
    /// its snapshot holds.
    /// </summary>
    [Fact]
    public void Both_sets_hold_the_same_migrations_in_the_Persistence_namespace()
    {
        string[] defaults = [.. MigrationFileNamesOf(DefaultSet).Select(WithoutId).Order(StringComparer.Ordinal)];
        string[] tenancy = [.. MigrationFileNamesOf(TenancySet).Select(WithoutId).Order(StringComparer.Ordinal)];

        defaults.ShouldNotBeEmpty($"no migration under {DefaultSet}; {Regenerate}");
        tenancy.ShouldBe(defaults, $"{TenancySet} and {DefaultSet} hold different migrations; {Regenerate}");
        Sets.SelectMany(FilesOf).Where(static path => !DeclaresTheMigrationsNamespace(File.ReadAllText(path)))
            .Select(static path => Path.GetFileName(path))
            .ShouldBeEmpty($"a shipped migration file is not in the namespace {MigrationsNamespace}; {Regenerate}");
    }

    /// <summary>Positive control: a file generated under another project's name, or in a subfolder's namespace, is not in the namespace.</summary>
    [Theory]
    [InlineData("namespace Trellis.Persistence.Migrations\n{\n}", true)]
    [InlineData("namespace Trellis.Persistence.Migrations;\n", true)]
    [InlineData("namespace Acme.Tenants.Persistence.Migrations\n{\n}", false)]
    [InlineData("namespace Trellis.Persistence.Migrations.Tenancy\n{\n}", false)]
    public void The_namespace_check_tells_the_Persistence_migrations_namespace_from_another(string code, bool declares)
        => DeclaresTheMigrationsNamespace(code).ShouldBe(declares);

    /// <summary>
    /// The two initial migrations describe one model: the multi-tenant snapshot is the default one plus the
    /// tenant seam (a <c>TenantId</c> property with its index on every aggregate root and on the users, the
    /// outbox's column, and <c>TenantId</c> leading a unique index an aggregate root declares, of which the
    /// reference model has none). A model change that regenerates one set alone fails here, in a run that needs
    /// no Docker, naming the lines only one snapshot holds; without this it would surface only in Template CI's
    /// multi-tenant legs, as a migration that no longer matches its model. Lines are compared as a multiset,
    /// because EF orders entities by name.
    /// </summary>
    [Fact]
    public void The_multi_tenant_snapshot_is_the_default_one_plus_the_tenant_seam()
    {
        string root = RepositorySource.Root.FullName;
        string defaultSnapshot = File.ReadAllText(Path.Combine(root, DefaultSet, Snapshot));
        string tenancySnapshot = File.ReadAllText(Path.Combine(root, TenancySet, Snapshot));

        tenancySnapshot.ShouldContain("\"TenantId\"", customMessage: $"{TenancySet}/{Snapshot} holds no tenant column: it is not the multi-tenant set; {Regenerate}");
        SeamDrift(defaultSnapshot, tenancySnapshot).ShouldBeEmpty(
            $"the two initial migrations no longer describe the same model; after a model change, {Regenerate}");
    }

    /// <summary>
    /// Positive control: a line only one snapshot holds is reported from either side, and the tenant seam is
    /// not (a statement that runs over several lines included). The rows name no store type: the database-engine
    /// scan reads this file.
    /// </summary>
    [Theory]
    [InlineData("b.Property<string>(\"Notes\");", "b.Property<string>(\"Notes\");", 0)]
    [InlineData("b.Property<string>(\"Notes\");", "", 1)]
    [InlineData("", "b.Property<string>(\"Notes\");", 1)]
    [InlineData("b.HasIndex(\"Number\").IsUnique();", "b.HasIndex(\"TenantId\", \"Number\").IsUnique();", 0)]
    [InlineData("", "b.Property<string>(\"TenantId\")\n    .IsRequired()\n    .HasMaxLength(64);\nb.HasIndex(\"TenantId\");", 0)]
    public void The_seam_check_reports_a_line_only_one_snapshot_holds(string defaultBody, string tenancyBody, int reported)
        => SeamDrift($"class S {{\n{defaultBody}\n}}", $"class S {{\n{tenancyBody}\n}}").Length.ShouldBe(reported);

    /// <summary>
    /// A generated project gets exactly one set. With the option, a source of its own lays the multi-tenant set
    /// over the Persistence project's migrations folder, and a modifier under the same condition keeps the
    /// default set out; without that modifier <c>dotnet new</c> still reports success and ships both. The remedy
    /// here is the wiring, not a regeneration, so the message names <c>template.json</c>'s sources.
    /// </summary>
    [Fact]
    public void The_template_ships_the_multi_tenant_set_in_place_of_the_default_one()
        => SetWiringProblems(File.ReadAllText(Path.Combine(RepositorySource.Root.FullName, TemplateConfig))).ShouldBeEmpty(
            $"{TemplateConfig}: with the option the multi-tenant set replaces the default one, and without it only the default one ships; "
            + "fix the sources of template.json (a source of the set conditional on the option, and a modifier of the first source that excludes the default set under the same condition)");

    /// <summary>Positive control: the wiring as shipped passes; each way to ship both sets, neither or the wrong one is reported.</summary>
    [Theory]
    [InlineData("(UseMultitenancy)", "(UseMultitenancy)", "./src/Infrastructure/Trellis.Persistence/Migrations/", "src/Infrastructure/Trellis.Persistence/Migrations/**", 0)]
    [InlineData("(UseMultitenancy)", "(UseMultitenancy)", "./src/Infrastructure/Trellis.Persistence/Migrations/", "src/Infrastructure/Trellis.Persistence/Migrations/*.Designer.cs", 1)]
    [InlineData("(UseMultitenancy)", "(!UseMultitenancy)", "./src/Infrastructure/Trellis.Persistence/Migrations/", "src/Infrastructure/Trellis.Persistence/Migrations/**", 1)]
    [InlineData(null, "(UseMultitenancy)", "./src/Infrastructure/Trellis.Persistence/Migrations/", "src/Infrastructure/Trellis.Persistence/Migrations/**", 1)]
    [InlineData("(UseMultitenancy)", "(UseMultitenancy)", "./src/Infrastructure/Trellis.Persistence/Migrations/Tenancy/", "src/Infrastructure/Trellis.Persistence/Migrations/**", 1)]
    public void The_wiring_check_flags_each_way_to_ship_both_sets_or_the_wrong_one(
        string? sourceCondition, string modifierCondition, string target, string excluded, int reported)
    {
        string config = $$"""
            { "sources": [
              { "source": "./", "exclude": [ ".template.config/**" ],
                "modifiers": [ { "condition": "{{modifierCondition}}", "exclude": [ "{{excluded}}" ] } ] },
              { "source": "./.template.config/consumer/", "target": "./" },
              { {{(sourceCondition is null ? string.Empty : $"\"condition\": \"{sourceCondition}\", ")}}"source": "./.template.config/tenancy/Migrations/", "target": "{{target}}" } ] }
            """;

        SetWiringProblems(config).Length.ShouldBe(reported);
    }

    private const string MigrationsNamespace = "Trellis.Persistence.Migrations";

    private const string Option = "(UseMultitenancy)";

    /// <summary>What is wrong with how <paramref name="templateJson"/> ships the two sets; empty when a project gets exactly one.</summary>
    private static string[] SetWiringProblems(string templateJson)
    {
        using JsonDocument document = JsonDocument.Parse(templateJson);
        JsonElement[] sources = [.. document.RootElement.GetProperty("sources").EnumerateArray()];
        JsonElement[] tenancy = [.. sources.Skip(1).Where(static source => Text(source, "source") == $"./{TenancySet}/")];
        if (tenancy.Length != 1)
        {
            return [$"{tenancy.Length} sources ship ./{TenancySet}/ (one must)"];
        }

        List<string> problems = [];
        if (Text(tenancy[0], "condition") != Option)
        {
            problems.Add($"the source of ./{TenancySet}/ is not conditional on {Option}: it would ship into every project");
        }

        if (Text(tenancy[0], "target") != $"./{DefaultSet}/")
        {
            problems.Add($"the source of ./{TenancySet}/ does not target ./{DefaultSet}/");
        }

        bool defaultSetLeftOut = sources[0].TryGetProperty("modifiers", out JsonElement modifiers)
            && modifiers.EnumerateArray().Any(static modifier => Text(modifier, "condition") == Option
                && modifier.TryGetProperty("exclude", out JsonElement exclude)
                && exclude.EnumerateArray().Any(static glob => glob.GetString() == $"{DefaultSet}/**"));
        if (!defaultSetLeftOut)
        {
            problems.Add($"no modifier of the first source excludes {DefaultSet}/** under {Option}: both sets would ship");
        }

        return [.. problems];

        static string? Text(JsonElement element, string property)
            => element.TryGetProperty(property, out JsonElement value) ? value.GetString() : null;
    }

    /// <summary>The lines one snapshot holds and the other does not, once the tenant seam is taken out of the second.</summary>
    private static string[] SeamDrift(string defaultSnapshot, string tenancySnapshot)
    {
        string withoutSeam = TenantIdStatement().Replace(tenancySnapshot.ReplaceLineEndings("\n"), string.Empty)
            .Replace("HasIndex(\"TenantId\", ", "HasIndex(", StringComparison.Ordinal);
        List<string> left = Lines(defaultSnapshot);
        List<string> right = Lines(withoutSeam);
        foreach (string line in right.ToList())
        {
            if (left.Remove(line))
            {
                right.Remove(line);
            }
        }

        return [.. left.Select(static line => $"only in the default snapshot: {line}"), .. right.Select(static line => $"only in the multi-tenant snapshot: {line}")];

        static List<string> Lines(string text)
            => [.. text.ReplaceLineEndings("\n").Split('\n').Select(static line => line.Trim()).Where(static line => line.Length > 0)];
    }

    private static bool DeclaresTheMigrationsNamespace(string code) => MigrationsNamespaceDeclaration().IsMatch(code);

    /// <summary>Every C# file of <paramref name="set"/>, by full path, in order; none when the folder is missing.</summary>
    private static string[] FilesOf(string set)
    {
        string folder = Path.Combine(RepositorySource.Root.FullName, set);
        return Directory.Exists(folder) ? [.. Directory.EnumerateFiles(folder, "*.cs").Order(StringComparer.Ordinal)] : [];
    }

    /// <summary>The file names of <paramref name="set"/>'s migrations, the model snapshot aside.</summary>
    private static IEnumerable<string> MigrationFileNamesOf(string set)
        => FilesOf(set).Select(static path => Path.GetFileName(path)).Where(static name => !name.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal));

    /// <summary><c>20261125094954_InitialCreate.Designer.cs</c> is <c>InitialCreate.Designer.cs</c>.</summary>
    private static string WithoutId(string fileName) => fileName[(fileName.IndexOf('_', StringComparison.Ordinal) + 1)..];

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

    [GeneratedRegex("""b\.(?:Property<string>\("TenantId"\)|HasIndex\("TenantId"\))[^;]*;""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TenantIdStatement();

    [GeneratedRegex("""^namespace Trellis\.Persistence\.Migrations\s*[;{]?\s*$""", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MigrationsNamespaceDeclaration();
}
