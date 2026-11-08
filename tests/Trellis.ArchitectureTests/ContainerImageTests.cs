using System.Text.RegularExpressions;
using Shouldly;
using Xunit;

namespace Trellis.ArchitectureTests;

/// <summary>
/// The license ceilings cover the container images the solution pulls, not only its packages (ADR-0011, amendment
/// of 2026-11-08): the AppHost's resources in run and publish mode, the services of the root compose file and the
/// workflows' containers. Each image is on <see cref="ImageAllowlist"/> with the license it ships under, read before
/// it was added. The case that prompted the rule: the Redis 8 server (RSALv2/SSPLv1/AGPLv3) and RedisInsight (SSPL),
/// the <c>--use-redis</c> cache and its tile until Valkey (BSD-3-Clause) replaced them.
/// <para>
/// A text scan, so it sees what is written: an <c>Add*</c> call of the AppHost pulls its hosting package's default
/// image (<see cref="ResourceImages"/>), a tool tile its own (<see cref="TileImages"/>), a <c>WithImage</c> or an
/// <c>AddContainer</c> the literal it names. A <c>WithImage</c> override is checked as an image of its own beside the
/// resource's default, which stays checked: the scan cannot tell which one a later call leaves in place. An image
/// computed at run time is refused, since its license cannot be read here.
/// </para>
/// </summary>
public sealed partial class ContainerImageTests
{
    private const string AppHostFolder = "tools/Trellis.AppHost/";

    /// <summary>
    /// Every image the solution may pull, keyed by its full name (registry and repository, no tag), each with its
    /// license and where it is used. An entry is a license decision: read the image's license before adding one.
    /// An image no file pulls today keeps its entry: an option that is off (<c>--use-redis false</c>) leaves its
    /// image unused, and the decision still stands.
    /// </summary>
    private static readonly Dictionary<string, string> ImageAllowlist = new(StringComparer.Ordinal)
    {
        ["docker.io/library/postgres"] = "PostgreSQL License: the database (AddPostgres; docker-compose.yml)",
        ["docker.io/dpage/pgadmin4"] = "PostgreSQL License: pgAdmin, the tile beside PostgreSQL (WithPgAdmin)",
        ["docker.io/valkey/valkey"] =
            "BSD-3-Clause: the Redis-protocol cache with --use-redis (AddValkey; the redis service of docker-compose.yml)",
        ["mcr.microsoft.com/dotnet/nightly/aspire-dashboard"] =
            "MIT: the Aspire dashboard the compose output runs (AddDockerComposeEnvironment)",
    };

    /// <summary>
    /// Files that may pull an image the allowlist does not name, keyed by repository-relative path, each with its
    /// reason. Empty on purpose: an image is admitted for the whole solution or not at all.
    /// </summary>
    private static readonly Dictionary<string, string> ExcusedFiles = new(StringComparer.Ordinal);

    /// <summary>
    /// The image each AppHost resource pulls unless a <c>WithImage</c> replaces it (its hosting package's
    /// <c>*ContainerImageTags</c>), keyed by the name after <c>Add</c>. A resource missing here fails the scan until
    /// it gets a row, or a place in <see cref="NotContainers"/> when it pulls no image.
    /// </summary>
    private static readonly Dictionary<string, string> ResourceImages = new(StringComparer.Ordinal)
    {
        ["Postgres"] = "docker.io/library/postgres",
        ["Valkey"] = "docker.io/valkey/valkey",
        ["Redis"] = "docker.io/library/redis",
        ["MailPit"] = "docker.io/axllent/mailpit",
        // The compose environment's dashboard, a service of the published compose file.
        ["DockerComposeEnvironment"] = "mcr.microsoft.com/dotnet/nightly/aspire-dashboard",
    };

    /// <summary>The <c>Add*</c> calls that pull no image: a process Aspire runs, a value, a child of a resource.</summary>
    private static readonly HashSet<string> NotContainers = new(StringComparer.Ordinal) { "Project", "Parameter", "Database", "ConnectionString" };

    /// <summary>
    /// The tool tiles a resource can start beside itself, each a container of its own, with the image it pulls. A
    /// tile method missing here is not seen: a new one gets a row.
    /// </summary>
    private static readonly Dictionary<string, string> TileImages = new(StringComparer.Ordinal)
    {
        ["PgAdmin"] = "docker.io/dpage/pgadmin4",
        ["PgWeb"] = "docker.io/sosedoff/pgweb",
        ["RedisInsight"] = "docker.io/redis/redisinsight",
        ["RedisCommander"] = "docker.io/joeferner/redis-commander",
        ["MongoExpress"] = "docker.io/library/mongo-express",
    };

    private static readonly string[] ComposeFiles = ["docker-compose.yml", "docker-compose.yaml", "compose.yml", "compose.yaml"];

    [Fact]
    public void Every_container_image_the_AppHost_declares_is_on_the_allowlist()
    {
        List<SourceFile> scanned = [.. ScannedFiles()];

        scanned.Where(static file => file.RelativePath.StartsWith(AppHostFolder, StringComparison.Ordinal))
            .ShouldNotBeEmpty($"the image scan found no C# file under {AppHostFolder}; is the repository root right?");
        scanned.SelectMany(PullsIn).ShouldNotBeEmpty("the image scan found no image at all; is the parser right?");
        SourceScan.StaleEntries(scanned, ExcusedFiles, static file => ProblemsIn(file).Select(static problem => problem.Index)).ShouldBeEmpty();

        List<string> violations = [.. scanned
            .Where(static file => !ExcusedFiles.ContainsKey(file.RelativePath))
            .SelectMany(static file => ProblemsIn(file).Select(problem => $"{file.Describe(problem.Index)} -> {problem.Text}"))];
        violations.ShouldBeEmpty(
            "every container image is a license decision (ADR-0011): read the image's license and, if it is free for a product "
            + "to ship, add it to ImageAllowlist with the license and where it is used; Redis 8 (RSALv2/SSPLv1/AGPLv3) and "
            + "RedisInsight (SSPL) are off the list, so the cache is Valkey with no browser tile");
    }

    /// <summary>
    /// Positive control for the image scan, which expects no image off the allowlist: the Redis server, the
    /// RedisInsight tile, an arbitrary container, an override, an image computed at run time and a resource the
    /// scan cannot map are flagged, in the AppHost and in YAML (an inert template branch, commented, included);
    /// what the AppHost and the compose file pull today is not. The scan reads the AppHost and the YAML files only,
    /// so the rows are written out whole.
    /// </summary>
    [Theory]
    [InlineData("var cache = builder.AddRedis(\"cache\");", AppHostFolder + "Program.cs", true)]
    [InlineData("var cache = builder.AddValkey(\"cache\").WithRedisInsight();", AppHostFolder + "Program.cs", true)]
    [InlineData("builder.AddContainer(\"grafana\", \"grafana/grafana\");", AppHostFolder + "Program.cs", true)]
    [InlineData("builder.AddContainer(\"grafana\", image);", AppHostFolder + "Program.cs", true)]
    [InlineData("builder.AddPostgres(\"pg\").WithImage(\"bitnami/postgresql\", \"17\");", AppHostFolder + "Program.cs", true)]
    [InlineData("builder.AddKafka(\"events\");", AppHostFolder + "Program.cs", true)]
    [InlineData("builder.AddPostgres(\"postgres\").WithImageTag(\"18\").WithDataVolume().WithPgAdmin();", AppHostFolder + "Program.cs", false)]
    [InlineData("var cache = builder.AddValkey(\"cache\").WithImageTag(\"9\");", AppHostFolder + "Program.cs", false)]
    [InlineData("builder.AddProject<Projects.Api>(\"api\").WithReference(database).WaitFor(database);", AppHostFolder + "Program.cs", false)]
    [InlineData("builder.AddContainer(\"db\", \"docker.io/library/postgres:18\");", AppHostFolder + "Program.cs", false)]
    [InlineData("services:\n  cache:\n    image: redis:8\n", "docker-compose.yml", true)]
    [InlineData("services:\n  cache:\n    image: \"redis/redisinsight:3.4\"\n", "docker-compose.yml", true)]
    [InlineData("services:\n  redis:\n    image: valkey/valkey:9\n  postgres:\n    image: postgres:18\n", "docker-compose.yml", false)]
    [InlineData("#    services:\n#      cache:\n#        image: redis:8\n", ".github/workflows/ci.yml", true)]
    [InlineData("jobs:\n  e2e:\n    container: grafana/grafana:11\n", ".github/workflows/ci.yml", true)]
    [InlineData("    steps:\n      - uses: docker://redis:8\n", ".github/workflows/ci.yml", true)]
    [InlineData("    steps:\n      - uses: actions/checkout@v5\n", ".github/workflows/ci.yml", false)]
    public void The_image_scan_flags_an_image_off_the_allowlist(string text, string path, bool flagged)
        => (ProblemsIn(new SourceFile(path, text)).Count > 0).ShouldBe(flagged);

    [Theory]
    [InlineData("postgres:18", "docker.io/library/postgres")]
    [InlineData("valkey/valkey:9", "docker.io/valkey/valkey")]
    [InlineData("docker.io/axllent/mailpit:v1.31.2", "docker.io/axllent/mailpit")]
    [InlineData("mcr.microsoft.com/dotnet/nightly/aspire-dashboard", "mcr.microsoft.com/dotnet/nightly/aspire-dashboard")]
    [InlineData("localhost:5000/team/app:1@sha256:0123", "localhost:5000/team/app")]
    [InlineData("Redis@sha256:abc", "docker.io/library/redis")]
    public void The_image_reader_takes_the_full_name_without_tag_or_digest(string written, string fullName)
        => FullName(written).ShouldBe(fullName);

    /// <summary>The AppHost's C# files, comments stripped, and every workflow and root compose file as written.</summary>
    private static IEnumerable<SourceFile> ScannedFiles()
    {
        foreach (SourceFile file in RepositorySource.CSharpFiles("tools")
                     .Where(static file => file.RelativePath.StartsWith(AppHostFolder, StringComparison.Ordinal)))
        {
            yield return file;
        }

        // YAML comments stay: an inert template branch is a comment here and a live line in generated output.
        foreach ((string relativePath, string fullPath) in RepositorySource.FilesUnder(".github/workflows")
                     .Where(static file => file.RelativePath.EndsWith(".yml", StringComparison.Ordinal)
                                           || file.RelativePath.EndsWith(".yaml", StringComparison.Ordinal)))
        {
            yield return new SourceFile(relativePath, File.ReadAllText(fullPath).ReplaceLineEndings("\n"));
        }

        foreach (string compose in ComposeFiles)
        {
            string fullPath = Path.Combine(RepositorySource.Root.FullName, compose);
            if (File.Exists(fullPath))
            {
                yield return new SourceFile(compose, File.ReadAllText(fullPath).ReplaceLineEndings("\n"));
            }
        }
    }

    private sealed record ImagePull(int Index, string? Image, string Via);

    private sealed record Problem(int Index, string Text);

    /// <summary>Every image <paramref name="file"/> pulls, as the full name; <see cref="ImagePull.Image"/> is null when the scan cannot tell which.</summary>
    private static IEnumerable<ImagePull> PullsIn(SourceFile file)
        => file.RelativePath.EndsWith(".cs", StringComparison.Ordinal) ? AppHostPullsIn(file.Code) : YamlPullsIn(file.Code);

    private static List<Problem> ProblemsIn(SourceFile file)
        => [.. PullsIn(file)
            .Where(static pull => pull.Image is null || !ImageAllowlist.ContainsKey(pull.Image))
            .Select(static pull => new Problem(pull.Index, pull.Image is null
                ? $"{pull.Via}: the scan cannot tell which image this pulls"
                : $"{pull.Via} pulls {pull.Image}, which ImageAllowlist does not name"))];

    private static IEnumerable<ImagePull> AppHostPullsIn(string code)
    {
        foreach (Match call in AddCall().Matches(code))
        {
            string resource = call.Groups["resource"].Value;
            if (NotContainers.Contains(resource) || resource == "Container")
            {
                continue;
            }

            yield return ResourceImages.TryGetValue(resource, out string? image)
                ? new ImagePull(call.Index, image, $"Add{resource}")
                : new ImagePull(call.Index, null,
                    $"Add{resource} (map it to its default image in ResourceImages, or name it in NotContainers if it pulls none)");
        }

        foreach (Match container in AddContainerCall().Matches(code))
        {
            yield return LiteralPull(container, "AddContainer");
        }

        foreach (Match image in WithImageCall().Matches(code))
        {
            yield return LiteralPull(image, "WithImage");
        }

        foreach (Match with in WithCall().Matches(code))
        {
            string tile = with.Groups["name"].Value;
            if (TileImages.TryGetValue(tile, out string? image))
            {
                yield return new ImagePull(with.Index, image, $"With{tile}");
            }
        }
    }

    private static ImagePull LiteralPull(Match call, string via)
    {
        string argument = call.Groups["image"].Value.Trim();
        return argument.Length >= 2 && argument[0] == '"' && argument[^1] == '"'
            ? new ImagePull(call.Index, FullName(argument[1..^1]), via)
            : new ImagePull(call.Index, null, $"{via}({argument}) (write the image as a string literal)");
    }

    private static IEnumerable<ImagePull> YamlPullsIn(string text)
    {
        foreach (Match line in YamlImage().Matches(text))
        {
            string image = line.Groups["image"].Value;
            // ${API_IMAGE} and the like: the solution's own images, built by dotnet publish.
            if (!image.StartsWith('$'))
            {
                yield return new ImagePull(line.Index, FullName(image), line.Groups["key"].Value.TrimEnd(':', '/'));
            }
        }
    }

    /// <summary>
    /// The full name of an image as written (<c>postgres:18</c> → <c>docker.io/library/postgres</c>): the digest and
    /// the tag dropped, Docker Hub's registry and its <c>library/</c> namespace added where the name leaves them out,
    /// lower case.
    /// </summary>
    private static string FullName(string written)
    {
        string name = written.Trim().ToLowerInvariant();
        int digest = name.IndexOf('@', StringComparison.Ordinal);
        if (digest >= 0)
        {
            name = name[..digest];
        }

        int tag = name.LastIndexOf(':');
        if (tag > name.LastIndexOf('/'))
        {
            name = name[..tag];
        }

        string[] segments = name.Split('/');
        if (segments.Length == 1)
        {
            return $"docker.io/library/{name}";
        }

        bool hasRegistry = segments[0].Contains('.', StringComparison.Ordinal)
            || segments[0].Contains(':', StringComparison.Ordinal)
            || segments[0] == "localhost";
        return hasRegistry ? name : $"docker.io/{name}";
    }

    [GeneratedRegex(@"\.Add(?<resource>[A-Z][A-Za-z0-9]*)\s*(?:<[^<>()]*>)?\s*\(", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AddCall();

    [GeneratedRegex(@"\.AddContainer\s*\(\s*(?:""[^""]*""|[^,()""]+?)\s*,\s*(?<image>""[^""]*""|[^,()]+)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AddContainerCall();

    [GeneratedRegex(@"\.WithImage\s*\(\s*(?<image>""[^""]*""|[^,()]+)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WithImageCall();

    [GeneratedRegex(@"\.With(?<name>[A-Z][A-Za-z0-9]*)\s*\(", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WithCall();

    /// <summary>
    /// An <c>image:</c> key, a job's <c>container:</c> written as one image, or a <c>docker://</c> step, after any
    /// indentation and comment hashes (an inert branch is commented in the template source).
    /// </summary>
    [GeneratedRegex(@"^[ \t#]*(?:-[ \t]+)?(?<key>image:|container:|uses:[ \t]*docker://)[ \t]*[""']?(?<image>[A-Za-z0-9$][^\s""'#]*)", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex YamlImage();
}
