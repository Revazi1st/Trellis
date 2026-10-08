using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Trellis.Api.Common.OpenApi;

/// <summary>
/// Money is <c>decimal</c> on the wire; the generator labels it <c>format: double</c>, which every
/// client generator maps to a binary float. <c>decimal</c> is in the OpenAPI format registry and is
/// what Kiota and NSwag map back to <c>System.Decimal</c>. The <c>number | string</c> type union stays:
/// it truthfully reflects that the host reads numbers from strings (<c>JsonSerializerDefaults.Web</c>).
/// </summary>
internal sealed class DecimalSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        Type type = Nullable.GetUnderlyingType(context.JsonTypeInfo.Type) ?? context.JsonTypeInfo.Type;
        if (type == typeof(decimal))
        {
            schema.Format = "decimal";
        }

        return Task.CompletedTask;
    }
}
