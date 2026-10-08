using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Trellis.Api.Common.Endpoints;

namespace Trellis.Api.Common.OpenApi;

/// <summary>
/// Documents the error contract (ADR-0024) on every operation from the operation's shape, so an endpoint
/// cannot forget it (ADR-0025): a 400 wherever there is input to reject, a 404 wherever a route id can
/// miss, a 409 on a write to an addressed resource (state rule or concurrency), 429 and 500 everywhere.
/// Every 4xx/5xx — including the 401/403 and the idempotency 409 earlier transformers add, and a status an anonymous
/// endpoint declares with a bare <c>.Produces(status)</c> because its shape cannot imply it — carries the
/// <c>application/problem+json</c> body: a reference to the <c>ProblemResponse</c> component generated
/// from the Contracts record the clients deserialise into. A 201 declares its <c>Location</c>; an endpoint marked
/// <c>SupportsConditionalGet()</c> declares <c>If-None-Match</c>, the <c>ETag</c> on its 200 and the 304.
/// Runs last, after the security and idempotency transformers, because it completes their entries.
/// </summary>
internal sealed class ProblemResponsesOperationTransformer : IOpenApiOperationTransformer
{
    private const string ProblemMediaType = "application/problem+json";

    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        // A reference, resolved when the document is written: ProblemComponentsDocumentTransformer publishes the schema.
        IOpenApiSchema problem = new OpenApiSchemaReference(ProblemComponentsDocumentTransformer.ProblemResponseSchemaId, context.Document);
        OpenApiResponses responses = operation.Responses ??= [];

        AddByShape(operation, context.Description, problem);

        // 401/403 (security transformer), the idempotency 409 and an endpoint's bare .Produces(status) arrive without a body
        // (the generator gives the last an empty content map): every error is a problem document.
        foreach ((string status, IOpenApiResponse response) in responses)
        {
            if (status is ['4' or '5', ..] && response is OpenApiResponse { Content: null or { Count: 0 } } concrete)
            {
                concrete.Content = ProblemContent(problem);
            }
        }

        if (responses.TryGetValue("201", out IOpenApiResponse? created) && created is OpenApiResponse createdResponse)
        {
            Header(createdResponse, "Location", "URL of the created resource.", "uri");
        }

        if (context.Description.ActionDescriptor.EndpointMetadata.OfType<ConditionalGetMetadata>().Any())
        {
            DocumentConditionalGet(operation);
        }

        operation.Responses = Sorted(responses);
        return Task.CompletedTask;
    }

    private static void AddByShape(OpenApiOperation operation, ApiDescription description, IOpenApiSchema problem)
    {
        OpenApiResponses responses = operation.Responses!;
        IList<ApiParameterDescription> parameters = description.ParameterDescriptions;
        bool hasRouteId = parameters.Any(static p => p.Source == BindingSource.Path);
        bool hasInput = hasRouteId || operation.RequestBody is not null || parameters.Any(static p => p.Source == BindingSource.Query);
        bool mutates = !HttpMethods.IsGet(description.HttpMethod ?? HttpMethods.Get);

        if (hasInput)
        {
            Add(responses, "400", "Validation failed or the request could not be read; `errors[]` names each offending field (`field` is null for a whole-request error).", problem);
        }

        if (hasRouteId)
        {
            Add(responses, "404", "No resource with this id.", problem);
        }

        if (mutates && hasRouteId)
        {
            Add(responses, "409", "The resource is not in a state that allows this operation, or it changed concurrently.", problem);
        }

        Add(responses, "429", "Rate limit exceeded; `Retry-After` says when to retry.", problem);
        Add(responses, "500", "Unexpected failure; quote `correlationId` or `traceId` when reporting it.", problem);
    }

    private static void DocumentConditionalGet(OpenApiOperation operation)
    {
        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = "If-None-Match",
            In = ParameterLocation.Header,
            Required = false,
            Description = "ETag(s) from a previous response (weak or strong form, a comma-separated list, or `*`); a match answers 304 with no body.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });

        OpenApiResponses responses = operation.Responses!;
        if (responses.TryGetValue("200", out IOpenApiResponse? ok) && ok is OpenApiResponse okResponse)
        {
            Header(okResponse, "ETag", "Weak validator derived from the row version; send it back as `If-None-Match`.", format: null);
        }

        responses.TryAdd("304", new OpenApiResponse { Description = "Not modified: an `If-None-Match` value matched the current ETag." });
    }

    /// <summary>Adds a problem response, or appends the sentence to one an earlier transformer created (the idempotency 409 keeps its own).</summary>
    private static void Add(OpenApiResponses responses, string status, string description, IOpenApiSchema problem)
    {
        if (responses.TryGetValue(status, out IOpenApiResponse? existing))
        {
            if (existing is OpenApiResponse response)
            {
                response.Description = $"{response.Description} {description}";
            }

            return;
        }

        responses[status] = new OpenApiResponse { Description = description, Content = ProblemContent(problem) };
    }

    private static void Header(OpenApiResponse response, string name, string description, string? format)
    {
        response.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal);
        response.Headers[name] = new OpenApiHeader
        {
            Description = description,
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = format },
        };
    }

    private static Dictionary<string, OpenApiMediaType> ProblemContent(IOpenApiSchema problem)
        => new(StringComparer.Ordinal) { [ProblemMediaType] = new OpenApiMediaType { Schema = problem } };

    /// <summary>Status order in the document, so a reader (and a diff) sees 200, 304, 400, 401 … rather than insertion order.</summary>
    private static OpenApiResponses Sorted(OpenApiResponses responses)
    {
        var sorted = new OpenApiResponses();
        foreach (string status in responses.Keys.Order(StringComparer.Ordinal))
        {
            sorted[status] = responses[status];
        }

        return sorted;
    }
}
