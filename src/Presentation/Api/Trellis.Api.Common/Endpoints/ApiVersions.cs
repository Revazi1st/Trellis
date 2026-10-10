using System.Globalization;
using Asp.Versioning;

namespace Trellis.Api.Common.Endpoints;

/// <summary>
/// The API versions this host serves — one source for the route group, the default version, the
/// OpenAPI document name and the concrete path segment the document declares (ADR-0025). Adding a v2
/// is additive: a <c>V2</c> here, <c>HasApiVersion(V2)</c> on the group and one more
/// <c>AddVersionedOpenApiDocument(V2)</c>.
/// </summary>
public static class ApiVersions
{
    public static readonly ApiVersion V1 = new(1, 0);

    /// <summary><c>v1</c>: the document name, hence <c>/openapi/v1.json</c> and <c>/scalar/v1</c>.</summary>
    public static string DocumentName(ApiVersion version) => "v" + PathSegment(version);

    /// <summary>
    /// <c>1</c>: what stands in for <c>{version}</c> in <c>/api/v{version}</c>, spelled the way
    /// <c>UrlSegmentApiVersionReader</c> parses it back (a minor version of 0 is implied; 1.1 stays <c>1.1</c>).
    /// </summary>
    public static string PathSegment(ApiVersion version)
    {
        int major = version.MajorVersion
            ?? throw new ArgumentException("A URL-segment API version needs a major version.", nameof(version));

        return version.MinorVersion is > 0 and int minor
            ? string.Create(CultureInfo.InvariantCulture, $"{major}.{minor}")
            : major.ToString(CultureInfo.InvariantCulture);
    }
}
