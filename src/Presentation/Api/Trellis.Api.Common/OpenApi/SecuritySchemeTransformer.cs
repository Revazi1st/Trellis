using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using Trellis.Api.Common.Idempotency;

namespace Trellis.Api.Common.OpenApi;

/// <summary>
/// Declares the JWT bearer scheme at document level — without it the document has no
/// securitySchemes at all, Scalar renders no Authorize control, and every protected endpoint is
/// untryable from the reference UI.
/// </summary>
internal sealed class SecuritySchemeTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        document.Components ??= new OpenApiComponents();
        document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>(StringComparer.Ordinal)
        {
            [JwtBearerDefaults.AuthenticationScheme] = new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                In = ParameterLocation.Header,
                BearerFormat = "JSON Web Token",
                Description = "Access token from POST /auth/login; rotate via POST /auth/refresh.",
            },
        };

        return Task.CompletedTask;
    }
}

/// <summary>
/// Marks every operation that actually requires authorization as bearer-secured, with the 401/403
/// outcomes documented. It reasons from what the endpoint declares (authorization data and no
/// anonymous opt-out) rather than from the mere absence of <see cref="IAllowAnonymous"/>, so an
/// endpoint that requires nothing is never advertised as secured.
/// </summary>
internal sealed class SecurityRequirementOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        IList<object> metadata = context.Description.ActionDescriptor.EndpointMetadata;
        bool secured = metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any();
        if (!secured)
        {
            return Task.CompletedTask;
        }

        operation.Security ??= [];
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            [new OpenApiSecuritySchemeReference(JwtBearerDefaults.AuthenticationScheme, context.Document)] = [],
        });

        operation.Responses ??= new OpenApiResponses();
        operation.Responses.TryAdd("401", new OpenApiResponse { Description = "Missing or invalid access token." });
        operation.Responses.TryAdd("403", new OpenApiResponse { Description = "Authenticated, but lacking the required permission." });

        return Task.CompletedTask;
    }
}

/// <summary>
/// Documents the Idempotency-Key header and the 409 duplicate outcome on endpoints marked by
/// <c>RequireIdempotency()</c> — otherwise consumers cannot discover the header from the spec.
/// </summary>
internal sealed class IdempotencyOperationTransformer : IOpenApiOperationTransformer
{
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        bool idempotent = context.Description.ActionDescriptor.EndpointMetadata.OfType<IdempotencyMetadata>().Any();
        if (!idempotent)
        {
            return Task.CompletedTask;
        }

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = IdempotencyFilter.HeaderName,
            In = ParameterLocation.Header,
            Required = false,
            Description = "Unique value per logical request; retry attempts reuse it (scoped to caller + route). "
                          + "Replaying an already-completed request returns 409; a failed request releases the key.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String, MaxLength = IdempotencyFilter.MaxKeyLength },
        });

        operation.Responses ??= new OpenApiResponses();
        operation.Responses.TryAdd("409", new OpenApiResponse { Description = "Duplicate request — this Idempotency-Key was already processed." });

        return Task.CompletedTask;
    }
}
