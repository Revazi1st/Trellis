using Asp.Versioning;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Trellis.Api.Common.Endpoints;

namespace Trellis.Api.Common.OpenApi;

/// <summary>
/// Makes the document describe one concrete API version (ADR-0025). ApiExplorer renders the group
/// template literally — <c>api/v{version}/…</c> with no declared <c>version</c> parameter — because
/// substituting it is the job of <c>Asp.Versioning</c>'s API-explorer package, which this host does not
/// reference. A URL-segment-versioned document has exactly one version, so the segment is substituted
/// here instead of asking every client to fill in a parameter. The generator also fills <c>servers</c>
/// from whatever address served the request; the committed contract must not pin a host, so it is dropped.
/// </summary>
internal sealed class VersionedDocumentTransformer(ApiVersion version) : IOpenApiDocumentTransformer
{
    private const string VersionToken = "{version}";

    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Servers = null;

        string segment = ApiVersions.PathSegment(version);
        var paths = new OpenApiPaths();
        foreach ((string template, IOpenApiPathItem item) in document.Paths)
        {
            paths.Add(template.Replace(VersionToken, segment, StringComparison.Ordinal), item);
        }

        document.Paths = paths;
        return Task.CompletedTask;
    }
}
