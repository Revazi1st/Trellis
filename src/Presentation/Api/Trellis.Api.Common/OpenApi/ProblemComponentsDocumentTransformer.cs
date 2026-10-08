using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Trellis.Api.Contracts.Common;

namespace Trellis.Api.Common.OpenApi;

/// <summary>
/// Publishes the error contract's schemas once, under <c>components.schemas</c>, generated from the
/// Contracts records the clients deserialise into (ADR-0024): <c>ProblemResponse</c>, whose
/// <c>errors[]</c> items reference <c>FieldError</c>. The generator only lifts schemas it met while
/// describing handler signatures; a problem body is produced by the pipeline, not declared by any
/// handler, so a transformer asking for the schema gets it inline — and twelve copies of it would be
/// the document. <see cref="ProblemResponsesOperationTransformer"/> points every 4xx/5xx at these two.
/// </summary>
internal sealed class ProblemComponentsDocumentTransformer : IOpenApiDocumentTransformer
{
    public const string ProblemResponseSchemaId = nameof(ProblemResponse);
    public const string FieldErrorSchemaId = nameof(FieldError);

    public async Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        IOpenApiSchema problem = await context.GetOrCreateSchemaAsync(typeof(ProblemResponse), null, cancellationToken);
        IOpenApiSchema fieldError = await context.GetOrCreateSchemaAsync(typeof(FieldError), null, cancellationToken);

        if (problem is OpenApiSchema problemSchema)
        {
            // The XML summary is written for a C# reader (cref links, history); the document gets the contract sentence.
            problemSchema.Description = "RFC 9457 problem document (ADR-0024): every 4xx/5xx response. Branch on `code` "
                + "(`type` is `urn:problem-type:{code}`); `title` and `detail` are for people; `errors[]` is present on a 400 produced "
                + "from a Result, absent otherwise; `correlationId` equals the X-Correlation-ID response header.";

            if (problemSchema.Properties?.TryGetValue("errors", out IOpenApiSchema? errors) == true && errors is OpenApiSchema errorsSchema)
            {
                errorsSchema.Items = new OpenApiSchemaReference(FieldErrorSchemaId, document);
            }
        }

        document.Components ??= new OpenApiComponents();
        document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        document.Components.Schemas[ProblemResponseSchemaId] = problem;
        document.Components.Schemas[FieldErrorSchemaId] = fieldError;
    }
}
