using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Trellis.Api.Common.Http;

namespace Trellis.Api.Common.OpenApi;

/// <summary>
/// Gives every <c>string</c> its <c>type</c> back. The schema generator describes only the serializer's
/// built-in converters, and <c>string</c> has one of the host's own (<see cref="NulFreeStringJsonConverter"/>,
/// which refuses U+0000), so it would otherwise leave each string an empty schema that accepts anything.
/// The type is the one the built-in converter documents: <c>string</c>, with <c>null</c> beside it for a
/// property declared nullable, which <c>OpenApiSnapshotTests</c> holds to the committed document.
/// </summary>
internal sealed class StringSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (context.JsonTypeInfo.Type == typeof(string) && schema.Type is null)
        {
            bool nullable = context.JsonPropertyInfo is { PropertyType: var declared } property
                            && declared == typeof(string)
                            && (property.IsGetNullable || property.IsSetNullable);
            schema.Type = nullable ? JsonSchemaType.String | JsonSchemaType.Null : JsonSchemaType.String;
        }

        return Task.CompletedTask;
    }
}
