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
    /// the engine rewrites the template's name wherever it stands. So the second set holds the migrations and
    /// the model snapshot the first holds, by name, and every file of both declares the Persistence project's
    /// migrations namespace: a set generated under another project's name would ship that name, in its
    /// namespace and in the CLR names its snapshot holds.
    /// </summary>
    [Fact]
    public void Both_sets_hold_the_same_migrations_in_the_Persistence_namespace()
    {
        FilesOf(DefaultSet).ShouldNotBeEmpty($"no migration under {DefaultSet}; {Regenerate}");
        SetDifferences(FilesOf(DefaultSet).Select(static path => Path.GetFileName(path)), FilesOf(TenancySet).Select(static path => Path.GetFileName(path)))
            .ShouldBeEmpty($"{TenancySet} and {DefaultSet} hold different migrations; {Regenerate}");
        Sets.SelectMany(FilesOf).Where(static path => !DeclaresTheMigrationsNamespace(File.ReadAllText(path)))
            .Select(static path => Path.GetRelativePath(RepositorySource.Root.FullName, path))
            .ShouldBeEmpty($"a shipped migration file is not in the namespace {MigrationsNamespace}; {Regenerate}");
    }

    /// <summary>
    /// Positive control: two sets of the same migrations under different ids are the same; a migration only one
    /// set holds, and a set that lost its model snapshot, are reported.
    /// </summary>
    [Theory]
    [InlineData("20261125094954_InitialCreate.cs 20261125094954_InitialCreate.Designer.cs AppDbContextModelSnapshot.cs", "20261125131916_InitialCreate.cs 20261125131916_InitialCreate.Designer.cs AppDbContextModelSnapshot.cs", 0)]
    [InlineData("20261125094954_InitialCreate.cs 20261125094954_InitialCreate.Designer.cs AppDbContextModelSnapshot.cs", "20261125131916_InitialCreate.cs 20261125131916_InitialCreate.Designer.cs 20261201090000_AddNotes.cs 20261201090000_AddNotes.Designer.cs AppDbContextModelSnapshot.cs", 2)]
    [InlineData("20261125094954_InitialCreate.cs 20261125094954_InitialCreate.Designer.cs AppDbContextModelSnapshot.cs", "20261125131916_InitialCreate.cs 20261125131916_InitialCreate.Designer.cs", 1)]
    public void The_set_check_reports_a_file_only_one_set_holds(string defaultFiles, string tenancyFiles, int reported)
        => SetDifferences(defaultFiles.Split(' '), tenancyFiles.Split(' ')).Length.ShouldBe(reported);

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
    /// tenant seam, so a change to the shared model that reaches one set alone fails here, in a run that needs
    /// no Docker, naming the statements only one snapshot holds; without this it would surface only in
    /// Template CI. What is taken out of the multi-tenant snapshot first, and nothing else: every statement of
    /// the <c>TenantId</c> column (on every aggregate root, the users and the outbox) and of an index on that
    /// column alone, whole; and a unique index an aggregate root declares, which the convention leads with
    /// <c>TenantId</c> (<c>AppDbContext.LeadWithTenant</c>), written back as the default snapshot writes it: the
    /// column out of the list, the tenant's ascending flag out of a descending one, and the database name out
    /// when it is EF's default one for that table and those columns, since the convention names the index
    /// explicitly to keep that name. A unique index declared with an EF name (<c>HasIndex(columns, name)</c>,
    /// which the snapshot writes as an array), or one with a per-column provider setting (a collation, an
    /// operator class, a NULLS order), differs further and is reported. Because the seam's statements go whole,
    /// a change of the seam itself passes here (the column's length or nullability, an index on it added or
    /// dropped, an aggregate root without the column), and so does a statement that moved to another entity:
    /// <c>scripts/regenerate-initial-migrations.sh --check</c> (Template CI's pack job) and
    /// <c>MigrationDriftTests</c> on Template CI's multi-tenant legs hold those. Statements are compared as a
    /// multiset, because an index led by <c>TenantId</c> sorts after <c>b.HasIndex("TenantId")</c>, where its
    /// default twin sorts by its own first column.
    /// </summary>
    [Fact]
    public void The_multi_tenant_snapshot_is_the_default_one_plus_the_tenant_seam()
    {
        string root = RepositorySource.Root.FullName;
        foreach (string set in Sets)
        {
            File.Exists(Path.Combine(root, set, Snapshot)).ShouldBeTrue($"{set}/{Snapshot} is missing; {Regenerate}");
        }

        string defaultSnapshot = File.ReadAllText(Path.Combine(root, DefaultSet, Snapshot));
        string tenancySnapshot = File.ReadAllText(Path.Combine(root, TenancySet, Snapshot));

        tenancySnapshot.ShouldContain("\"TenantId\"", customMessage: $"{TenancySet}/{Snapshot} holds no tenant column: it is not the multi-tenant set; {Regenerate}");
        SeamDrift(defaultSnapshot, tenancySnapshot).ShouldBeEmpty(
            $"the two initial migrations no longer describe the same model; after a model change, {Regenerate}. When both sets "
            + "are freshly regenerated (the script's --check passes), the statements above are a shape of the tenant seam "
            + "this fact does not take out yet: teach it to SeamDrift");
    }

    /// <summary>
    /// Positive control: a statement only one snapshot holds is reported from either side, and the tenant seam is
    /// not (a statement that runs over several lines included). The unique-index rows are what a regeneration
    /// wrote for an index with the default name, one with a pinned name, a descending one and a two-column one
    /// that is descending on its first column (order as written); an index both sides declare differently for
    /// another reason (a name pinned on one side, a sort order or a uniqueness only one side has) is reported
    /// once from each. The rows name no store type: the database-engine scan reads this file.
    /// </summary>
    [Theory]
    [InlineData("b.Property<string>(\"Notes\");", "b.Property<string>(\"Notes\");", 0)]
    [InlineData("b.Property<string>(\"Notes\");", "", 1)]
    [InlineData("", "b.Property<string>(\"Notes\");", 1)]
    [InlineData("", "b.Property<string>(\"TenantId\")\n    .IsRequired()\n    .HasMaxLength(64);\nb.HasIndex(\"TenantId\");", 0)]
    [InlineData(
        "b.HasIndex(\"CreatedBy\")\n    .IsUnique();\n\nb.HasIndex(\"Status\");",
        "b.HasIndex(\"Status\");\n\nb.HasIndex(\"TenantId\");\n\nb.HasIndex(\"TenantId\", \"CreatedBy\")\n    .IsUnique()\n    .HasDatabaseName(\"IX_Invoices_CreatedBy\");",
        0)]
    [InlineData(
        "b.HasIndex(\"CustomerName\")\n    .IsUnique()\n    .HasDatabaseName(\"UX_Invoices_CustomerName\");",
        "b.HasIndex(\"TenantId\", \"CustomerName\")\n    .IsUnique()\n    .HasDatabaseName(\"UX_Invoices_CustomerName\");",
        0)]
    [InlineData(
        "b.HasIndex(\"ModifiedBy\")\n    .IsUnique()\n    .IsDescending();",
        "b.HasIndex(\"TenantId\", \"ModifiedBy\")\n    .IsUnique()\n    .IsDescending(false, true)\n    .HasDatabaseName(\"IX_Invoices_ModifiedBy\");",
        0)]
    [InlineData(
        "b.HasIndex(\"ModifiedBy\", \"Status\")\n    .IsUnique()\n    .IsDescending(true, false);",
        "b.HasIndex(\"TenantId\", \"ModifiedBy\", \"Status\")\n    .IsUnique()\n    .IsDescending(false, true, false)\n    .HasDatabaseName(\"IX_Invoices_ModifiedBy_Status\");",
        0)]
    [InlineData(
        "b.HasIndex(\"CreatedBy\")\n    .IsUnique();",
        "b.HasIndex(\"TenantId\", \"CreatedBy\")\n    .IsUnique()\n    .HasDatabaseName(\"UX_Invoices_CreatedBy\");",
        2)]
    [InlineData(
        "b.HasIndex(\"CustomerName\")\n    .IsUnique()\n    .HasDatabaseName(\"UX_Invoices_CustomerName\");",
        "b.HasIndex(\"TenantId\", \"CustomerName\")\n    .IsUnique()\n    .HasDatabaseName(\"IX_Invoices_CustomerName\");",
        2)]
    [InlineData(
        "b.HasIndex(\"ModifiedBy\")\n    .IsUnique();",
        "b.HasIndex(\"TenantId\", \"ModifiedBy\")\n    .IsUnique()\n    .IsDescending(false, true)\n    .HasDatabaseName(\"IX_Invoices_ModifiedBy\");",
        2)]
    [InlineData(
        "b.HasIndex(\"CreatedBy\");",
        "b.HasIndex(\"TenantId\", \"CreatedBy\")\n    .IsUnique()\n    .HasDatabaseName(\"IX_Invoices_CreatedBy\");",
        2)]
    public void The_seam_check_reports_a_statement_only_one_snapshot_holds(string defaultBody, string tenancyBody, int reported)
        => SeamDrift(Entity(defaultBody), Entity(tenancyBody)).Length.ShouldBe(reported);

    /// <summary>An entity of a snapshot, as EF writes it: its table comes after its indexes.</summary>
    private static string Entity(string body) => $$"""
        modelBuilder.Entity("Trellis.Domain.Invoices.Invoice", b =>
            {
        {{body}}

                b.ToTable("Invoices", (string)null);
            });
        """;

    /// <summary>
    /// A generated project gets exactly one set. With the option, a source of its own lays the multi-tenant set
    /// over the Persistence project's migrations folder, and a modifier under the same condition keeps the
    /// default set out; without that modifier <c>dotnet new</c> still reports success and ships both. Every
    /// source after the first that ships the set or lands in that folder is read (a second set fails here until
    /// this fact and <see cref="Sets"/> learn it), and so is every exclude of the first source and of its
    /// modifiers (only the one under the option may leave the default set out). The remedy here is the wiring,
    /// not a regeneration, so the message names <c>template.json</c>'s sources.
    /// </summary>
    [Fact]
    public void The_template_ships_the_multi_tenant_set_in_place_of_the_default_one()
        => SetWiringProblems(File.ReadAllText(Path.Combine(RepositorySource.Root.FullName, TemplateConfig))).ShouldBeEmpty(
            $"{TemplateConfig}: with the option the multi-tenant set replaces the default one, and without it only the default one ships; "
            + "fix the sources of template.json (a source of the set conditional on the option, and a modifier of the first source that excludes the default set under the same condition)");

    private const string ShippedSet =
        """{ "condition": "(UseMultitenancy)", "source": "./.template.config/tenancy/Migrations/", "target": "./src/Infrastructure/Trellis.Persistence/Migrations/" }""";

    private const string ShippedModifier =
        """{ "condition": "(UseMultitenancy)", "exclude": [ "src/Infrastructure/Trellis.Persistence/Migrations/**" ] }""";

    /// <summary>
    /// Positive control: the wiring as shipped passes; each way to ship both sets, neither, the wrong one or a
    /// part of one is reported, by the problem the row is about. <paramref name="sets"/> are the sources after the
    /// consumer's, <paramref name="modifiers"/> the first source's.
    /// </summary>
    [Theory]
    [InlineData(ShippedSet, ShippedModifier, null)]
    [InlineData(ShippedSet, """{ "condition": "(UseMultitenancy)", "exclude": [ "src/Infrastructure/Trellis.Persistence/Migrations/*.Designer.cs" ] }""", "no modifier of the first source excludes")]
    [InlineData(ShippedSet, """{ "condition": "(!UseMultitenancy)", "exclude": [ "src/Infrastructure/Trellis.Persistence/Migrations/**" ] }""", "under (!UseMultitenancy)")]
    [InlineData(ShippedSet, ShippedModifier + """, { "condition": "(UseRedis)", "exclude": [ "src/Infrastructure/**" ] }""", "excludes src/Infrastructure/** under (UseRedis)")]
    [InlineData("""{ "source": "./.template.config/tenancy/Migrations/", "target": "./src/Infrastructure/Trellis.Persistence/Migrations/" }""", ShippedModifier, "is not conditional on")]
    [InlineData("""{ "condition": "(UseMultitenancy)", "source": "./.template.config/tenancy/Migrations/", "target": "./src/Infrastructure/Trellis.Persistence/Migrations/Tenancy/" }""", ShippedModifier, "does not target")]
    [InlineData("""{ "condition": "(UseMultitenancy)", "source": "./.template.config/tenancy/Migrations/", "target": "./src/Infrastructure/Trellis.Persistence/Migrations/", "exclude": [ "*.Designer.cs" ] }""", ShippedModifier, "carries exclude")]
    [InlineData("""{ "condition": "(UseMultitenancy)", "source": "./.template.config/tenancy/Migrations/", "target": "./src/Infrastructure/Trellis.Persistence/Migrations/", "modifiers": [ { "condition": "(UseMultitenancy)", "exclude": [ "src/Infrastructure/Trellis.Persistence/Migrations/**" ] } ] }""", "", "no modifier of the first source excludes")]
    [InlineData("", ShippedModifier, "0 sources after the first")]
    [InlineData(ShippedSet + """, { "condition": "(UseRedis)", "source": "./.template.config/redis/Migrations/", "target": "src/Infrastructure/Trellis.Persistence/Migrations" }""", ShippedModifier, "2 sources after the first")]
    [InlineData("""{ "condition": "(UseMultitenancy)", "source": "./.template.config/other/Migrations/", "target": "./src/Infrastructure/Trellis.Persistence/Migrations/" }""", ShippedModifier, "and none ships")]
    public void The_wiring_check_flags_each_way_to_ship_both_sets_or_the_wrong_one(string sets, string modifiers, string? reported)
    {
        string config = $$"""
            { "sources": [
              { "source": "./", "exclude": [ ".template.config/**" ], "modifiers": [ {{modifiers}} ] },
              { "source": "./.template.config/consumer/", "target": "./" }{{(sets.Length == 0 ? string.Empty : $", {sets}")}} ] }
            """;

        string[] problems = SetWiringProblems(config);

        if (reported is null)
        {
            problems.ShouldBeEmpty();
        }
        else
        {
            problems.ShouldContain(problem => problem.Contains(reported, StringComparison.Ordinal), $"no problem names '{reported}' among: {string.Join(" | ", problems)}");
        }
    }

    private const string MigrationsNamespace = "Trellis.Persistence.Migrations";

    private const string Option = "(UseMultitenancy)";

    /// <summary>What is wrong with how <paramref name="templateJson"/> ships the two sets; empty when a project gets exactly one.</summary>
    private static string[] SetWiringProblems(string templateJson)
    {
        using JsonDocument document = JsonDocument.Parse(templateJson);
        JsonElement[] sources = [.. document.RootElement.GetProperty("sources").EnumerateArray()];
        List<string> problems = [];

        // Every source after the first that ships the set or lands in the default set's folder, however its
        // target is spelled: one may, the multi-tenant set's own.
        JsonElement[] sets = [.. sources.Skip(1).Where(static source => Text(source, "source") == $"./{TenancySet}/" || LandsInTheDefaultSet(Text(source, "target")))];
        if (sets.Length != 1)
        {
            problems.Add($"{sets.Length} sources after the first ship ./{TenancySet}/ or land in ./{DefaultSet}/ (one must, the multi-tenant set; "
                + $"a set for another option is taught to this fact and to {nameof(Sets)} first)");
        }
        else
        {
            JsonElement set = sets[0];
            if (Text(set, "source") != $"./{TenancySet}/")
            {
                problems.Add($"the source {Text(set, "source")} lands in ./{DefaultSet}/, and none ships ./{TenancySet}/");
            }

            if (Text(set, "condition") != Option)
            {
                problems.Add($"the source of ./{TenancySet}/ is not conditional on {Option}: it would ship into every project");
            }

            if (Text(set, "target") != $"./{DefaultSet}/")
            {
                problems.Add($"the source of ./{TenancySet}/ does not target ./{DefaultSet}/");
            }

            string[] narrowings = ["include", "exclude", "modifiers"];
            foreach (string narrowing in narrowings.Where(property => set.TryGetProperty(property, out _)))
            {
                problems.Add($"the source of ./{TenancySet}/ carries {narrowing}: every file of the set ships, the snapshot with the migration");
            }
        }

        // The first source: only a modifier under the option may leave the default set out, and it leaves all of it out.
        JsonElement first = sources[0];
        List<(string? Condition, string Glob)> excludes = [.. Globs(first, "exclude").Select(static glob => ((string?)null, glob))];
        if (first.TryGetProperty("modifiers", out JsonElement modifiers))
        {
            foreach (JsonElement modifier in modifiers.EnumerateArray())
            {
                excludes.AddRange(Globs(modifier, "exclude").Select(glob => (Text(modifier, "condition"), glob)));
            }
        }

        foreach ((string? condition, string glob) in excludes.Where(static exclude => exclude.Condition != Option && ExcludesFromTheDefaultSet(exclude.Glob)))
        {
            problems.Add($"the first source excludes {glob} {(condition is null ? "with no condition" : $"under {condition}")}: only {DefaultSet}/** under {Option} may leave the default set out");
        }

        if (!excludes.Any(static exclude => exclude.Condition == Option && exclude.Glob == $"{DefaultSet}/**"))
        {
            problems.Add($"no modifier of the first source excludes {DefaultSet}/** under {Option}: both sets would ship");
        }

        return [.. problems];

        static string? Text(JsonElement element, string property)
            => element.TryGetProperty(property, out JsonElement value) ? value.GetString() : null;

        static IEnumerable<string> Globs(JsonElement element, string property)
            => element.TryGetProperty(property, out JsonElement list) ? list.EnumerateArray().Select(static glob => glob.GetString()).OfType<string>() : [];
    }

    /// <summary>Whether a source's <c>target</c> is the default set's folder or a folder under it, spelled with or without <c>./</c> and a final slash.</summary>
    private static bool LandsInTheDefaultSet(string? target)
    {
        if (target is null)
        {
            return false;
        }

        string folder = (target.StartsWith("./", StringComparison.Ordinal) ? target[2..] : target).TrimEnd('/');
        return folder == DefaultSet || folder.StartsWith($"{DefaultSet}/", StringComparison.Ordinal);
    }

    /// <summary>Whether an exclude glob of the first source (<c>*</c> within a folder, <c>**</c> across them) takes a file of the default set.</summary>
    private static bool ExcludesFromTheDefaultSet(string glob)
    {
        Regex pattern = new($"^{Regex.Escape(glob).Replace(@"\*\*", "\u0001", StringComparison.Ordinal).Replace(@"\*", "[^/]*", StringComparison.Ordinal).Replace("\u0001", ".*", StringComparison.Ordinal).Replace(@"\?", "[^/]", StringComparison.Ordinal)}$", RegexOptions.None, TimeSpan.FromSeconds(1));
        return FilesOfASet.Any(file => pattern.IsMatch($"{DefaultSet}/{file}"));
    }

    /// <summary>The three files a set holds, under a made-up id.</summary>
    private static readonly string[] FilesOfASet = [Snapshot, "20260101000000_InitialCreate.cs", "20260101000000_InitialCreate.Designer.cs"];

    /// <summary>
    /// The statements one snapshot holds and the other does not, once the tenant seam is taken out of the second:
    /// a statement of the <c>TenantId</c> column or of its own index goes whole, and a unique index led by
    /// <c>TenantId</c> is written back as the default snapshot declares it (<see cref="WithoutTheTenantLead"/>).
    /// </summary>
    private static string[] SeamDrift(string defaultSnapshot, string tenancySnapshot)
    {
        List<string> tenancy = Statements(tenancySnapshot);
        List<string> withoutSeam = [];
        for (int i = 0; i < tenancy.Count; i++)
        {
            if (!tenancy[i].StartsWith(TenantColumn, StringComparison.Ordinal) && !tenancy[i].StartsWith(TenantIndex, StringComparison.Ordinal))
            {
                withoutSeam.Add(WithoutTheTenantLead(tenancy[i], TableAfter(tenancy, i)));
            }
        }

        (List<string> defaultOnly, List<string> tenancyOnly) = OnlyInOne(Statements(defaultSnapshot), withoutSeam);
        return [.. defaultOnly.Select(static statement => $"only in the default snapshot: {statement}"),
            .. tenancyOnly.Select(static statement => $"only in the multi-tenant snapshot: {statement}")];
    }

    private const string TenantColumn = "b.Property<string>(\"TenantId\")";

    private const string TenantIndex = "b.HasIndex(\"TenantId\")";

    /// <summary>
    /// <paramref name="statement"/> as the default snapshot writes it when it is a unique index the convention led
    /// with <c>TenantId</c> (<c>AppDbContext.LeadWithTenant</c>), otherwise unchanged. The convention puts the
    /// column first, keeps a descending index's flags behind an ascending one for the tenant, and names the index
    /// explicitly with the name it had, which the default snapshot writes only when it is not EF's default
    /// (<c>IX_{table}_{columns}</c>): the column, that flag and that default name come out, and nothing else.
    /// EF writes an index that is descending on every column as <c>.IsDescending()</c>.
    /// </summary>
    private static string WithoutTheTenantLead(string statement, string? table)
    {
        Match lead = TenantLedIndex().Match(statement);
        if (!lead.Success)
        {
            return statement;
        }

        string columns = lead.Groups["columns"].Value;
        string calls = TenantLedSortOrder().Replace(lead.Groups["calls"].Value, static order =>
            order.Groups["others"].Value.Split(", ").All(static flag => flag == "true") ? ".IsDescending()" : $".IsDescending({order.Groups["others"].Value})");
        if (table is not null)
        {
            string defaultName = $"IX_{table}_{string.Join('_', columns.Split(", ").Select(static column => column.Trim('"')))}";
            calls = calls.Replace($".HasDatabaseName(\"{defaultName}\")", string.Empty, StringComparison.Ordinal);
        }

        return $"b.HasIndex({columns}){calls}";
    }

    /// <summary>The table of the entity whose statement is at <paramref name="index"/>: EF writes it after the entity's indexes.</summary>
    private static string? TableAfter(List<string> statements, int index)
    {
        foreach (string statement in statements.Skip(index + 1))
        {
            if (statement.StartsWith("modelBuilder.Entity(", StringComparison.Ordinal))
            {
                return null;
            }

            if (EntityTable().Match(statement) is { Success: true } table)
            {
                return table.Groups["table"].Value;
            }
        }

        return null;
    }

    /// <summary>
    /// The statements of C# <paramref name="code"/>, each on one line: its trimmed lines joined up to the one that
    /// ends it, a semicolon, a lambda's arrow or a brace (a statement over several lines is one chained call).
    /// </summary>
    private static List<string> Statements(string code)
    {
        List<string> statements = [];
        List<string> current = [];
        foreach (string line in code.ReplaceLineEndings("\n").Split('\n').Select(static line => line.Trim()).Where(static line => line.Length > 0))
        {
            current.Add(line);
            if (line.EndsWith(';') || line.EndsWith("=>", StringComparison.Ordinal) || line.EndsWith('{') || line.EndsWith('}'))
            {
                statements.Add(string.Concat(current));
                current.Clear();
            }
        }

        if (current.Count > 0)
        {
            statements.Add(string.Concat(current));
        }

        return statements;
    }

    /// <summary>What only <paramref name="left"/> holds and what only <paramref name="right"/> holds, compared as multisets.</summary>
    private static (List<string> LeftOnly, List<string> RightOnly) OnlyInOne(IEnumerable<string> left, IEnumerable<string> right)
    {
        List<string> leftOnly = [.. left];
        List<string> rightOnly = [];
        foreach (string item in right)
        {
            if (!leftOnly.Remove(item))
            {
                rightOnly.Add(item);
            }
        }

        return (leftOnly, rightOnly);
    }

    /// <summary>The files only one set holds, by name once the id is cut off.</summary>
    private static string[] SetDifferences(IEnumerable<string> defaultFiles, IEnumerable<string> tenancyFiles)
    {
        (List<string> defaultOnly, List<string> tenancyOnly) = OnlyInOne(defaultFiles.Select(WithoutId), tenancyFiles.Select(WithoutId));
        return [.. defaultOnly.Select(static file => $"only in {DefaultSet}: {file}"), .. tenancyOnly.Select(static file => $"only in {TenancySet}: {file}")];
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

    [GeneratedRegex("""^b\.HasIndex\("TenantId", (?<columns>"[^"]+"(?:, "[^"]+")*)\)(?<calls>.*)$""", RegexOptions.Singleline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TenantLedIndex();

    [GeneratedRegex("""\.IsDescending\(false, (?<others>[^)]*)\)""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TenantLedSortOrder();

    [GeneratedRegex("""^b\.ToTable\("(?<table>[^"]+)""", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex EntityTable();

    [GeneratedRegex("""^namespace Trellis\.Persistence\.Migrations\s*[;{]?\s*$""", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MigrationsNamespaceDeclaration();
}
