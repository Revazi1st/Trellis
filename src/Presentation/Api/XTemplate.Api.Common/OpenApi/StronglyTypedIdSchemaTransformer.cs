using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using XTemplate.SharedKernel.Domain;

namespace XTemplate.Api.Common.OpenApi;

/// <summary>Documents every IStronglyTypedId&lt;T&gt; as a plain uuid string instead of an object schema.</summary>
internal sealed class StronglyTypedIdSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        bool isStronglyTypedId = context.JsonTypeInfo.Type.IsValueType
            && context.JsonTypeInfo.Type.GetInterfaces().Any(static i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStronglyTypedId<>));

        if (isStronglyTypedId)
        {
            schema.Type = JsonSchemaType.String;
            schema.Format = "uuid";
            schema.Properties?.Clear();
        }

        return Task.CompletedTask;
    }
}
