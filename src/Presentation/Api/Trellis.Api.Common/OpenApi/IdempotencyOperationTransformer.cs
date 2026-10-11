using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Trellis.Api.Common.Idempotency;

namespace Trellis.Api.Common.OpenApi;

/// <summary>
/// Documents the <c>Idempotency-Key</c> contract (ADR-0018) on every endpoint marked by
/// <c>RequireIdempotency()</c>: the header, the replay header on its success responses, the 409 (in
/// flight, or completed but not replayable) and the 422 (key reused for a different request) —
/// otherwise consumers cannot discover any of it from the spec.
/// </summary>
internal sealed class IdempotencyOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        IdempotencyMetadata? idempotency = context.Description.ActionDescriptor.EndpointMetadata.OfType<IdempotencyMetadata>().LastOrDefault();
        if (idempotency is null)
        {
            return Task.CompletedTask;
        }

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = IdempotencyFilter.HeaderName,
            In = ParameterLocation.Header,
            Required = idempotency.KeyRequired,
            Description =
                "Unique value per logical request; a retry repeats the request with the same key. Scoped to the caller, the method and the route. "
                + "A retry of a completed request is answered with the original status, body and Location plus `Idempotency-Replayed: true`; "
                + "the same key with a different method, path or body is a 422; a retry while the first request is still running is a 409 with Retry-After. "
                + "A failed request leaves no reservation, so its key may be retried as is. "
                + $"Responses larger than {idempotency.MaxReplayBodyBytes} bytes are not stored: a retry of such a request is a 409 (Idempotency.Duplicate) and the resource must be read instead. "
                + (idempotency.KeyRequired
                    ? "This endpoint requires the header: a request without it is a 400 (Idempotency.KeyRequired). "
                    : "Omit the header to send the request without idempotency. ")
                + "A blank value, a repeated header or a comma-separated list is a 400 (Idempotency.InvalidKey).",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = IdempotencyFilter.MaxKeyLength },
        });

        operation.Responses ??= new OpenApiResponses();
        foreach ((string status, IOpenApiResponse response) in operation.Responses)
        {
            if (status.StartsWith('2') && response is OpenApiResponse success)
            {
                success.Headers ??= new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal);
                success.Headers.TryAdd(IdempotencyFilter.ReplayedHeaderName, new OpenApiHeader
                {
                    Description = "`true` when this response is the stored outcome of an earlier request with the same Idempotency-Key, not a new execution. Absent otherwise.",
                    Schema = new OpenApiSchema { Type = JsonSchemaType.Boolean },
                });
            }
        }

        Describe(operation.Responses, "409",
            "Idempotency-Key conflict: the first request with this key is still running (`Idempotency.InFlight`, retry after `Retry-After`), "
            + "or it completed but its response cannot be replayed (`Idempotency.Duplicate`: read the resource).");
        Describe(operation.Responses, "422",
            "This Idempotency-Key was already used for a different request — another method, path or body (`Idempotency.FingerprintMismatch`).");

        return Task.CompletedTask;
    }

    /// <summary>Adds the response, or appends to the description of one another transformer already declared (the by-shape problem responses).</summary>
    private static void Describe(OpenApiResponses responses, string status, string description)
    {
        if (!responses.TryGetValue(status, out IOpenApiResponse? existing))
        {
            responses[status] = new OpenApiResponse { Description = description };
            return;
        }

        if (existing is OpenApiResponse response && response.Description?.Contains(description, StringComparison.Ordinal) != true)
        {
            response.Description = string.IsNullOrWhiteSpace(response.Description) ? description : $"{response.Description} {description}";
        }
    }
}
