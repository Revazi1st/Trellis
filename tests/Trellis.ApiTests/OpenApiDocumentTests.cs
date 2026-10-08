using System.Text.Json;
using Shouldly;
using Xunit;

namespace Trellis.ApiTests;

/// <summary>
/// Quality gates on the committed OpenAPI contract (<c>docs/api/openapi.v1.json</c>, ADR-0013).
/// <para>
/// These complement rather than duplicate the CI contract-diff step. That step regenerates the
/// document against the running app and fails on any change, which catches an accidental wire
/// change — but it is perfectly happy for a brand-new endpoint to arrive undocumented, because an
/// addition is still "a change you have to commit". These tests are what make the document's
/// CONTENT a gate: Scalar is the console this API ships with, and an operation with no prose in it
/// is an operation nobody can use without reading the source.
/// </para>
/// </summary>
public sealed class OpenApiDocumentTests
{
    private static readonly string[] HttpMethods = ["get", "post", "put", "patch", "delete"];

    [Fact]
    public void Every_operation_carries_a_summary_and_a_description()
    {
        List<string> undocumented = [];

        foreach ((string path, JsonElement item) in Operations())
        {
            foreach (JsonProperty operation in item.EnumerateObject())
            {
                if (!HttpMethods.Contains(operation.Name, StringComparer.Ordinal))
                {
                    continue;
                }

                bool hasSummary = Has(operation.Value, "summary");
                bool hasDescription = Has(operation.Value, "description");

                if (!hasSummary || !hasDescription)
                {
                    undocumented.Add($"{operation.Name.ToUpperInvariant()} {path}");
                }
            }
        }

        undocumented.ShouldBeEmpty(
            "every endpoint needs .WithSummary() and .WithDescription(); then regenerate "
            + $"docs/api/openapi.v1.json with scripts/export-openapi.sh. Missing: {string.Join(", ", undocumented)}");
    }

    [Fact]
    public void Health_endpoints_stay_out_of_the_public_contract()
    {
        // /health and /alive are infrastructure probes mapped outside the versioned API group. If
        // they ever appear here it means they were accidentally described (and, with a document-level
        // security requirement, would be advertised as authenticated routes they are not).
        IReadOnlyList<string> paths = [.. Operations().Select(static entry => entry.Path)];

        paths.ShouldNotContain(static path => string.Equals(path, "/health", StringComparison.Ordinal));
        paths.ShouldNotContain(static path => string.Equals(path, "/alive", StringComparison.Ordinal));
    }

    [Fact]
    public void The_document_is_the_v1_contract_and_is_not_empty()
    {
        using JsonDocument document = ReadDocument();

        document.RootElement.GetProperty("info").GetProperty("version").GetString().ShouldBe("1.0.0");
        Operations().ShouldNotBeEmpty();
    }

    private static bool Has(JsonElement operation, string property) =>
        operation.TryGetProperty(property, out JsonElement value)
        && !string.IsNullOrWhiteSpace(value.GetString());

    private static List<(string Path, JsonElement Item)> Operations()
    {
        using JsonDocument document = ReadDocument();

        return [.. document.RootElement.GetProperty("paths").EnumerateObject()
            .Select(static path => (path.Name, path.Value.Clone()))];
    }

    private static JsonDocument ReadDocument() => JsonDocument.Parse(File.ReadAllText(CommittedDocumentPath()));

    /// <summary>The committed contract, located by walking up from the test output directory (also used by the snapshot test's update mode).</summary>
    internal static string CommittedDocumentPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "docs", "api", "openapi.v1.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException("docs/api/openapi.v1.json was not found above the test output directory.");
    }
}
