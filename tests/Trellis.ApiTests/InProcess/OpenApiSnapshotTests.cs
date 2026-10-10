using System.Text.Json;
using System.Text.Json.Nodes;
using Shouldly;
using Xunit;

namespace Trellis.ApiTests.InProcess;

/// <summary>
/// The committed contract (<c>docs/api/openapi.v1.json</c>) is what consumers generate clients from, so
/// it must be the document the API actually serves — not last month's (ADR-0025). This test fetches
/// <c>/openapi/v1.json</c> from the in-process host (Development, so the document is mapped) and compares
/// it byte for byte with the committed file (the gate CI's diff applies too; the JSON walk only names where
/// they part); it runs on every <c>dotnet test</c>, inside the SDK container
/// too, where the CI step that boots the app and curls it cannot. Set <c>UPDATE_OPENAPI_SNAPSHOT=1</c>
/// to overwrite the committed file with the live document instead (then review the diff and commit it);
/// <c>scripts/export-openapi.sh</c> does the same against a running API.
/// </summary>
public sealed class OpenApiSnapshotTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    public const string UpdateVariable = "UPDATE_OPENAPI_SNAPSHOT";

    [Fact]
    public async Task Committed_document_matches_the_running_api()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string path = OpenApiDocumentTests.CommittedDocumentPath();

        string live = await factory.CreateClient().GetStringAsync("/openapi/v1.json", ct);

        if (string.Equals(Environment.GetEnvironmentVariable(UpdateVariable), "1", StringComparison.Ordinal))
        {
            // The server's own serialisation is the committed format, plus the final newline every text file
            // in the repository ends with (.editorconfig): byte for byte what export-openapi.sh writes.
            await File.WriteAllTextAsync(path, live + "\n", ct);
            return;
        }

        string committedText = await File.ReadAllTextAsync(path, ct);
        if (string.Equals(committedText, live + "\n", StringComparison.Ordinal))
        {
            return;
        }

        JsonNode liveDocument = JsonNode.Parse(live).ShouldNotBeNull();
        JsonNode committed = JsonNode.Parse(committedText).ShouldNotBeNull();
        string difference = FirstDifference(committed, liveDocument, "$")
            ?? "formatting only: the same JSON in different bytes (indentation, line endings or the final newline)";

        Assert.Fail(
            $"docs/api/openapi.v1.json no longer matches the document the API serves (first difference at {difference}). "
            + $"If the change is intended, regenerate it: {UpdateVariable}=1 dotnet test tests/Trellis.ApiTests "
            + "(or scripts/export-openapi.sh against a running API), review the diff and commit.");
    }

    /// <summary>JSON pointer of the first node that differs, with both sides spelled out, or null when the trees are equal.</summary>
    private static string? FirstDifference(JsonNode? committed, JsonNode? live, string path)
    {
        if (JsonNode.DeepEquals(committed, live))
        {
            return null;
        }

        switch (committed, live)
        {
            case (JsonObject expected, JsonObject actual):
                foreach (string key in expected.Select(static p => p.Key).Union(actual.Select(static p => p.Key), StringComparer.Ordinal))
                {
                    if (!expected.ContainsKey(key))
                    {
                        return $"{path}.{key} (only served)";
                    }

                    if (!actual.ContainsKey(key))
                    {
                        return $"{path}.{key} (only committed)";
                    }

                    string? inner = FirstDifference(expected[key], actual[key], $"{path}.{key}");
                    if (inner is not null)
                    {
                        return inner;
                    }
                }

                return path;

            case (JsonArray expected, JsonArray actual):
                for (int i = 0; i < Math.Min(expected.Count, actual.Count); i++)
                {
                    string? inner = FirstDifference(expected[i], actual[i], $"{path}[{i}]");
                    if (inner is not null)
                    {
                        return inner;
                    }
                }

                return $"{path} (committed {expected.Count} items, served {actual.Count})";

            default:
                return $"{path}: committed {Render(committed)}, served {Render(live)}";
        }
    }

    private static string Render(JsonNode? node)
        => node?.ToJsonString(new JsonSerializerOptions { WriteIndented = false }) ?? "null";
}
