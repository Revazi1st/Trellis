using System.Reflection;
using System.Text.RegularExpressions;
using Mono.Cecil;
using NetArchTest.Rules;
using Shouldly;
using Xunit;

namespace Trellis.ArchitectureTests;

/// <summary>
/// PostgreSQL is the one database engine, and what only it understands has one home per kind of code
/// (ADR-0033): <c>PostgresDialect</c> in Persistence, the host wiring, and <c>PostgresTestDatabase</c> in the
/// tests. Two facts hold that. The source scan reads every C# file under <c>src/</c>, <c>tools/</c> and
/// <c>tests/</c> for the provider's name, a store type or SQL text, and its allowlist is the list of files a
/// move to another engine starts from (<c>docs/switching-database.md</c>, which a third fact holds to the
/// list). The compiled check reads which types of the Persistence and Api assemblies call into the
/// provider's assemblies, because a provider extension method need not carry the provider's name (an
/// index's access method, a function on <c>EF.Functions</c>).
/// <para>
/// What neither fact sees: a connection string, wherever it is written; a container a test starts through
/// Testcontainers' generic <c>ContainerBuilder</c> (the builder names no engine, and the image is a
/// lower-case literal); a provider extension method called in <c>tools/</c> or <c>tests/</c> without the
/// provider's name in the file (the compiled check reads two assemblies, and the text scan alone holds the
/// rest); a store type or SQL handed to a member the pattern does not know (an annotation set by its key
/// with <c>HasAnnotation</c>, say), or written in an attribute form the pattern does not expect (an
/// attribute list such as <c>[Required, Timestamp]</c>, or the attribute with its namespace); the range of
/// versions the <c>If-Match</c> parser accepts (<c>TryGetExpectedVersion</c> reads a <c>uint</c>, the
/// widest row version this engine issues); and code that is neutral in text and in its references but
/// relies on how the engine behaves. The guide lists those by hand.
/// </para>
/// <para>
/// The migrations folder is exempt as a whole, by its path in the scan and by its namespace in the
/// compiled check, so a class written by hand there is seen by neither.
/// </para>
/// </summary>
public sealed partial class DatabaseEngineTests
{
    /// <summary>
    /// The files that may name the database engine or hand it a store type or SQL text, keyed by
    /// repository-relative path, each with its reason. The generated migrations are outside the scan.
    /// </summary>
    private static readonly Dictionary<string, string> EngineAllowlist = new(StringComparer.Ordinal)
    {
        ["src/Infrastructure/Trellis.Persistence/PostgresDialect.cs"] =
            "the seam: everything in Persistence that only PostgreSQL understands",
        ["src/Presentation/Api/Trellis.Api/Program.cs"] =
            "host wiring: Aspire's client integration for the provider (tracing and metrics), a package only a host references",
        ["src/Shared/Trellis.ServiceDefaults/ServiceNames.cs"] =
            "the Aspire resource name of the database server, which the AppHost and its tests read",
        ["tools/Trellis.AppHost/Program.cs"] =
            "the resource model: the server, its image tag, its volume and its admin tool",
        ["tools/Trellis.MigrationService/Program.cs"] =
            "host wiring, as in the API: Aspire's client integration for the provider",
        ["tests/TestSupport/Database/PostgresTestDatabase.cs"] =
            "the test seam: the container, the provider and the reading of its errors, behind ITestDatabase",
        ["tests/Trellis.IntegrationTests/DatabaseFixture.cs"] =
            "Respawn's adapter and the schema it empties; only IntegrationTests references Respawn",
        ["tests/Trellis.IntegrationTests/OutboxRetryTests.Runbook.cs"] =
            "runs the re-drive statement of docs/deployment.md (Outbox operations) as written there",
        ["tests/Trellis.IntegrationTests/TenancyTests.cs"] =
            "with multi-tenancy: the tenant-led index keeps the provider's per-column settings, in the model and in the DDL",
        ["tests/TestSupport/AppHost/AppHostTestingExtensions.cs"] =
            "swaps the AppHost's database server for a throwaway one, found by its resource name",
        ["tests/Trellis.ApiTests/ThrowawayDatabaseTests.cs"] =
            "pins that swap against an AppHost model with the same resource",
        ["tests/Trellis.ApiTests/AppHostStartTests.cs"] =
            "reads the AppHost model: the server among the resources a start watches, its admin tool's resource among those it does not",
        ["tests/Trellis.ArchitectureTests/ContainerImageTests.cs"] =
            "the image allowlist names the server's image and the AppHost call that pulls it",
    };

    /// <summary>
    /// Entries of <see cref="EngineAllowlist"/> for a file only an option ships. The template's repository
    /// holds the file while it builds without the option, and a project generated without the option does
    /// not have it, so a directive around the entry would compile it out where the file is. Such an entry is
    /// checked for staleness only where its file exists.
    /// </summary>
    private static readonly HashSet<string> ShippedOnlyWithAnOption = new(StringComparer.Ordinal)
    {
        "tests/Trellis.IntegrationTests/TenancyTests.cs",
    };

    [Fact]
    public void Only_the_listed_files_name_the_database_engine_or_write_its_SQL()
    {
        // A configuration, a repository, a processor or a test calls a member of the dialect (or asks the
        // test database) instead of naming the provider, a store type or SQL. The migrations are generated
        // by the provider and are outside the scan.
        List<SourceFile> scanned = Scanned();

        scanned.ShouldNotBeEmpty("the database-engine scan found no file under src/, tools/ or tests/; is the repository root right?");
        SourceScan.StaleEntries(scanned, EntriesToHold(scanned), EngineSpecificsIn).ShouldBeEmpty();
        SourceScan.Violations(scanned, EngineAllowlist, EngineSpecificsIn).ShouldBeEmpty(
            "in src/, move the provider call, the store type or the SQL into a member of PostgresDialect and call it; in tests/, "
            + "ask the fixture's ITestDatabase (.claude/rules/persistence.md and tests.md, ADR-0033). Code that cannot live "
            + "there goes on EngineAllowlist with its reason, and into docs/switching-database.md. (A solution whose own name "
            + "contains the engine's or the provider's name is a hit on every namespace line: exclude that name in EngineSpecific().)");
    }

    [Fact]
    public void The_switching_guide_names_every_file_on_the_list()
    {
        // The allowlist is the list of C# files that name the engine, and the guide is where a reader looks
        // for it: an entry the guide does not name is a place a move to another engine would miss.
        string path = Path.Combine(RepositorySource.Root.FullName, Guide);
        File.Exists(path).ShouldBeTrue($"{Guide} is the checklist a move to another engine starts from, and it is missing");
        string guide = File.ReadAllText(path);

        MissingFromGuide(guide, EntriesToHold(Scanned()).Keys).ShouldBeEmpty(
            $"name each of these files in {Guide}, with what a move to another engine changes there");
    }

    /// <summary>Positive control for the guide check: a path the guide does not spell out in full is reported, one it names is not.</summary>
    [Theory]
    [InlineData("Change `src/One.cs` and `tests/Two.cs`.", null)]
    [InlineData("Change `src/One.cs` and Two.cs.", "tests/Two.cs")]
    public void The_guide_check_reports_a_listed_file_the_guide_does_not_name(string guide, string? missing)
        => MissingFromGuide(guide, ["src/One.cs", "tests/Two.cs"]).ShouldBe(missing is null ? [] : [missing]);

    /// <summary>
    /// Positive control for the scan, which expects zero hits outside its allowlist: the provider's name in a
    /// using, a call, an exception type or an annotation, the engine's name in an Aspire call, in the
    /// Testcontainers module, in the health-check package and in a class named after a seam, each call that
    /// hands the database a store type, a collation, SQL text or a row version (through the fluent builder or
    /// the metadata API, with a nested type argument, or with its dot at the end of the line before), and the
    /// attribute and ADO.NET forms of the same are found; a call into the dialect or the test database, prose
    /// that spells the engine's name in a string, a query filter, a neutral mapping, a comparison of a type
    /// name and a member of that name with an expression body are not. The scan reads this file, so every
    /// flagged row is assembled from two literals.
    /// </summary>
    [Theory]
    [InlineData("using Npg" + "sql;", true)]
    [InlineData("options.UseNpg" + "sql(connectionString);", true)]
    [InlineData("catch (DbUpdateException e) when (e.InnerException is Postg" + "resException)", true)]
    [InlineData("index.SetAnnotation(\"Npg" + "sql:IndexOperators\", operators);", true)]
    [InlineData("var server = builder.AddPostg" + "res(\"db\");", true)]
    [InlineData("builder.Property(static o => o.Notes).HasColumn" + "Type(\"jsonb\");", true)]
    [InlineData("builder.HasIndex(static o => o.Code).HasFil" + "ter(\"\\\"DeletedAtUtc\\\" IS NULL\");", true)]
    [InlineData("builder.Property(static o => o.CreatedAtUtc).HasDefaultValue" + "Sql(\"now()\");", true)]
    [InlineData("builder.Property(static o => o.Total).HasComputedColumn" + "Sql(\"\\\"Net\\\" + \\\"Tax\\\"\");", true)]
    [InlineData("table.HasCheck" + "Constraint(\"CK_Orders_Total\", \"\\\"Total\\\" >= 0\");", true)]
    [InlineData("var rows = context.Orders.From" + "Sql($\"SELECT * FROM orders\");", true)]
    [InlineData("await context.Database.Execute" + "SqlAsync($\"DELETE FROM orders\", ct);", true)]
    [InlineData("await context.Database.Execute" + "SqlRawAsync(sql, ct);", true)]
    [InlineData("var count = context.Database.Sql" + "Query<int>($\"SELECT 1\");", true)]
    [InlineData("builder.Property(static o => o.Version).IsRow" + "Version();", true)]
    [InlineData("using Testcontainers.Postgre" + "Sql;", true)]
    [InlineData("var server = new Postgre" + "SqlBuilder(\"postgres:18\").Build();", true)]
    [InlineData("checks.AddNpg" + "Sql(connectionString);", true)]
    [InlineData("configurationBuilder.Properties<decimal>().HaveColumn" + "Type(\"numeric(18,2)\");", true)]
    [InlineData("builder.Property(static o => o.Name).UseCollat" + "ion(\"und-x-icu\");", true)]
    [InlineData("[Column(Type" + "Name = \"jsonb\")]", true)]
    [InlineData("command.Command" + "Text = \"SELECT 1 FOR UPDATE SKIP LOCKED\";", true)]
    [InlineData("[Time" + "stamp]", true)]
    [InlineData("[Time" + "stampAttribute]", true)]
    [InlineData("builder.ToSql" + "Query(\"SELECT * FROM orders\");", true)]
    [InlineData("public sealed class Postg" + "resDialectTests", true)]
    [InlineData("builder.Property(static o => o.Notes).Metadata.SetColumn" + "Type(\"jsonb\");", true)]
    [InlineData("builder.HasIndex(static o => o.Code).Metadata.SetFil" + "ter(\"\\\"DeletedAtUtc\\\" IS NULL\");", true)]
    [InlineData("property.SetDefaultValue" + "Sql(\"now()\");", true)]
    [InlineData("property.SetComputedColumn" + "Sql(\"\\\"Net\\\" + \\\"Tax\\\"\");", true)]
    [InlineData("property.SetCollat" + "ion(\"und-x-icu\");", true)]
    [InlineData("modelBuilder.HasCollat" + "ion(\"ci\", locale: \"und-u-ks-level2\", provider: \"icu\", deterministic: false);", true)]
    [InlineData("var rows = context.Database.Sql" + "Query<Dictionary<string, int>>($\"SELECT 1\");", true)]
    [InlineData("builder.Property(static o => o.Notes).\n        HasColumn" + "Type(\"jsonb\");", true)]
    [InlineData("PostgresDialect.MapJson(builder.Property(static m => m.Content));", false)]
    [InlineData("private readonly ITestDatabase _database = new PostgresTestDatabase(null);", false)]
    [InlineData("logger.LogInformation(\"Connected to PostgreSQL\");", false)]
    [InlineData("builder.HasQueryFilter(\"SoftDeleted\", static o => o.DeletedAtUtc == null);", false)]
    [InlineData("builder.Property(static o => o.Total).HasPrecision(18, 2);", false)]
    [InlineData("builder.Property(static m => m.NextAttemptAtUtc).IsConcurrencyToken();", false)]
    [InlineData("if (mapping.TypeName == expected) { return; }", false)]
    [InlineData("public string TypeName => \"orders.paid.v1\";", false)]
    [InlineData("public string CommandText => _text;", false)]
    public void The_database_engine_scan_flags_the_provider_a_store_type_and_SQL_text(string code, bool flagged)
        => EngineSpecificsIn(new SourceFile("Planted.cs", code)).Any().ShouldBe(flagged);

    /// <summary>
    /// Positive control for the entries an option ships: one whose file is absent is not held, and one whose
    /// file is there but no longer trips the scan is stale like any other.
    /// </summary>
    [Fact]
    public void An_entry_for_a_file_an_option_ships_is_held_only_where_the_file_exists()
    {
        string optional = ShippedOnlyWithAnOption.First();
        SourceFile dialect = new("src/Infrastructure/Trellis.Persistence/PostgresDialect.cs", "using Npg" + "sql;");

        EntriesToHold([dialect]).ShouldNotContainKey(optional);
        EntriesToHold([dialect, new SourceFile(optional, "// nothing of the engine's")]).ShouldContainKey(optional);
        SourceScan.StaleEntries([new SourceFile(optional, "int neutral = 1;")], EntriesToHold([new SourceFile(optional, "int neutral = 1;")]), EngineSpecificsIn)
            .ShouldContain(entry => entry.StartsWith(optional, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(AssembliesTheCheckReads))]
    public void Only_the_dialect_and_the_migrations_call_into_the_provider(string layer)
    {
        Assembly assembly = layer == Architecture.Persistence ? Architecture.PersistenceAssembly : Architecture.ApiAssembly;

        ProviderCallersOutsideTheDialect(assembly).ShouldBeEmpty(
            "call a member of PostgresDialect instead; a provider extension method is the dialect's to call, whatever its name");
    }

    /// <summary>
    /// The Persistence and Api assemblies. The migration worker and the test projects compile against the
    /// provider too, and this project references neither: there the text scan is the only guard.
    /// </summary>
    public static TheoryData<string> AssembliesTheCheckReads => [Architecture.Persistence, Architecture.Presentation];

    /// <summary>
    /// Positive control for the compiled check, and proof that it reads something: the dialect itself is
    /// found by name; the exemption covers the dialect and the migrations and not the rest of Persistence; a
    /// planted call to a provider extension whose name does not say whose it is fails the fact's own filter,
    /// written directly, inside a lambda and inside an async method (the compiler moves the last two into
    /// nested types of its own making, where most Persistence and Api code runs), and the failure names the
    /// provider member it reached; and a mapping written with EF Core's own members is not reported.
    /// </summary>
    [Fact]
    public void The_provider_check_finds_the_dialect_and_a_provider_call_that_does_not_name_the_provider()
    {
        ProviderCallersIn(Architecture.PersistenceAssembly)
            .ShouldContain(static type => type.FullName == Architecture.Persistence + ".PostgresDialect");
        Type registration = Architecture.PersistenceAssembly.GetType(Architecture.Persistence + ".DependencyInjection", throwOnError: true)!;
        IsTheDialectOrAMigration(registration).ShouldBeFalse("the exemption is the dialect and the migrations; the rest of Persistence is held");

        string[] planted = ProviderCallersOutsideTheDialect(typeof(PlantedViolations).Assembly);
        planted.ShouldContain(line => Reports(line, typeof(PlantedViolations.IndexMethodOutsideTheDialect), ".HasMethod"));
        planted.ShouldContain(line => Reports(line, typeof(PlantedViolations.ProviderCallInALambda), ".HasMethod"));
        planted.ShouldContain(line => Reports(line, typeof(PlantedViolations.ProviderCallInAnAsyncMethod), ".ILike"));
        planted.ShouldNotContain(line => line.StartsWith(typeof(PlantedViolations.NeutralIndexMapping).FullName + ":", StringComparison.Ordinal));
    }

    /// <summary>True when a line of the compiled check's failure is about <paramref name="caller"/> and names <paramref name="providerMember"/>.</summary>
    private static bool Reports(string line, Type caller, string providerMember)
        => line.StartsWith(caller.FullName + ": ", StringComparison.Ordinal) && line.Contains(providerMember, StringComparison.Ordinal);

    private const string Guide = "docs/switching-database.md";

    /// <summary>Every C# file under <c>src/</c>, <c>tools/</c> and <c>tests/</c>, the generated migrations aside.</summary>
    private static List<SourceFile> Scanned()
        => [.. RepositorySource.CSharpFiles("src", "tools", "tests").Where(static file => !GeneratedMigration().IsMatch(file.RelativePath))];

    /// <summary>The paths <paramref name="guide"/> does not contain in full, in order.</summary>
    private static string[] MissingFromGuide(string guide, IEnumerable<string> paths)
        => [.. paths.Where(path => !guide.Contains(path, StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    private static Dictionary<string, string> EntriesToHold(IReadOnlyCollection<SourceFile> scanned)
        => EngineAllowlist
            .Where(entry => !ShippedOnlyWithAnOption.Contains(entry.Key) || scanned.Any(file => file.RelativePath == entry.Key))
            .ToDictionary(StringComparer.Ordinal);

    private static IEnumerable<int> EngineSpecificsIn(SourceFile file) => EngineSpecific().Matches(file.Code).Select(static m => m.Index);

    private static bool IsTheDialectOrAMigration(Type type)
    {
        Type outermost = type;
        while (outermost.DeclaringType is { } declaring)
        {
            outermost = declaring;
        }

        return outermost.FullName == Architecture.Persistence + ".PostgresDialect"
            || outermost.Namespace == Architecture.Persistence + ".Migrations";
    }

    /// <summary>
    /// The fact's result, which its control shares: each type of <paramref name="assembly"/> outside the
    /// dialect and the migrations that uses the provider, as its full name and what it uses
    /// (<c>method -&gt; provider member</c>), because a call whose name does not say whose it is cannot be found by
    /// searching for the provider's name.
    /// </summary>
    private static string[] ProviderCallersOutsideTheDialect(Assembly assembly)
    {
        Type[] callers = [.. ProviderCallersIn(assembly).Where(static type => !IsTheDialectOrAMigration(type))];
        if (callers.Length == 0)
        {
            return [];
        }

        using ModuleDefinition module = ModuleDefinition.ReadModule(assembly.Location);
        return [.. callers.Select(type => module.GetType(type.FullName!.Replace('+', '/')) is { } definition
            ? $"{type.FullName}: {string.Join("; ", ProviderUsesIn(definition).Distinct(StringComparer.Ordinal))}"
            : type.FullName)];
    }

    /// <summary>The types of <paramref name="assembly"/> with a member whose signature or body uses a type of the provider's assemblies.</summary>
    private static IEnumerable<Type> ProviderCallersIn(Assembly assembly)
        => Types.InAssembly(assembly).That().MeetCustomRule(static type => ProviderUsesIn(type).Any()).GetTypes()
            .Select(static type => type.ReflectionType)
            .OfType<Type>();

    /// <summary>
    /// What the type's own members, and those of the nested types the compiler made for it, use of the
    /// provider's assemblies, each as <c>method -&gt; provider type or member</c>. The body of a lambda, of an
    /// async method and of an iterator is compiled into such a type (<c>&lt;&gt;c</c>, <c>&lt;Name&gt;d__0</c>), so the
    /// rule reads it with the type that owns it.
    /// </summary>
    private static IEnumerable<string> ProviderUsesIn(TypeDefinition type)
        => type.Fields
            .Where(static field => IsTheProviders(field.FieldType))
            .Select(field => $"{type.Name}.{field.Name} -> {field.FieldType.Name}")
            .Concat(type.Methods.SelectMany(method => ProviderUsesInMethod(method).Select(used => $"{type.Name}.{method.Name} -> {used}")))
            .Concat(type.NestedTypes.Where(static nested => nested.Name.StartsWith('<')).SelectMany(ProviderUsesIn));

    /// <summary>The provider's types and members that a method's signature, locals and instructions use.</summary>
    private static IEnumerable<string> ProviderUsesInMethod(MethodDefinition method)
        => method.Parameters.Select(static parameter => parameter.ParameterType)
            .Append(method.ReturnType)
            .Concat(method.HasBody ? method.Body.Variables.Select(static variable => variable.VariableType) : [])
            .Where(IsTheProviders)
            .Select(static used => used.Name)
            .Concat(method.HasBody
                ? method.Body.Instructions
                    .Select(static instruction => instruction.Operand switch
                    {
                        TypeReference used when IsTheProviders(used) => used.Name,
                        MemberReference member when IsTheProviders(member.DeclaringType) => $"{member.DeclaringType.Name}.{member.Name}",
                        _ => null,
                    })
                    .OfType<string>()
                : []);

    /// <summary>True for a type declared in an assembly named <c>Npgsql</c> or <c>Npgsql.*</c>, or built from one.</summary>
    private static bool IsTheProviders(TypeReference? type)
    {
        if (type is null)
        {
            return false;
        }

        if (type is GenericInstanceType generic && generic.GenericArguments.Any(IsTheProviders))
        {
            return true;
        }

        return type.GetElementType().Scope is AssemblyNameReference assembly
            && (assembly.Name == ProviderAssembly || assembly.Name.StartsWith(ProviderAssembly + ".", StringComparison.Ordinal));
    }

    private const string ProviderAssembly = "Npg" + "sql";

    /// <summary>The migrations folder of the Persistence project: generated by the provider, never hand-edited.</summary>
    [GeneratedRegex(@"^src/Infrastructure/[^/]+\.Persistence/Migrations/", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GeneratedMigration();

    /// <summary>
    /// What only one database engine understands, as text can show it: the provider's name anywhere (a using,
    /// its <c>Use…</c> call, an exception type, an annotation's name, the health check's spelling); the
    /// engine's name in an identifier, the two seams' own names aside, in the two spellings code uses (the
    /// Testcontainers module's among them; case-sensitive, so prose that spells PostgreSQL in a string is not
    /// a hit); a call that takes a store type, a collation, SQL text or a database-generated row version (a
    /// column type, also before conventions, an index filter, a default, a computed column, a check
    /// constraint, a raw query or command), through the fluent builder or the metadata API's <c>Set…</c>
    /// twin; and the same written as a mapping attribute or as an ADO.NET command's text. A member of your
    /// own named <c>TypeName</c> or <c>CommandText</c> is a hit where it is assigned or initialised, not where
    /// it is read or has an expression body. Each name is written with a one-letter class so that this
    /// pattern does not match its own text.
    /// </summary>
    [GeneratedRegex(
        @"N[p]g[sS]ql|P[o]stgres(?!Dialect\b|TestDatabase\b)|P[o]stgre[S]ql"
        + @"|\.\s*(?:(?:Has|Set)Column[T]ype|HaveColumn[T]ype|(?:Has|Use|Set)Collat[i]on|(?:Has|Set)Fil[t]er|(?:Has|Set)DefaultValue[S]ql|(?:Has|Set)ComputedColumn[S]ql|HasCheck[C]onstraint|IsRow[V]ersion|From[S]ql\w*|Sql[Q]uery\w*|Execute[S]ql\w*|ToSql[Q]uery)\s*(?:<(?:[^<>()]|<[^<>()]*>)*>\s*)?\("
        + @"|\b(?:Type[N]ame|Command[T]ext)\s*=(?![=>])|\[Time[s]tamp(?:Attribute)?\b",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex EngineSpecific();
}
