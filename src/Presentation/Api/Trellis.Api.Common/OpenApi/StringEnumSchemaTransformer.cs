using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Trellis.Api.Common.OpenApi;

/// <summary>
/// An enum serialised by name (<c>JsonStringEnumConverter</c>) comes out of the generator as
/// <c>enum: ["Draft", …]</c> with no <c>type</c>; a reader has to infer it from the values. Stating
/// <c>type: string</c> is what makes every client generator emit an enum type rather than a free string.
/// </summary>
internal sealed class StringEnumSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        Type type = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;
        if (type.IsEnum && schema.Type is null && schema.Enum is { Count: > 0 })
        {
            schema.Type = JsonSchemaType.String;
        }

        return Task.CompletedTask;
    }
}
