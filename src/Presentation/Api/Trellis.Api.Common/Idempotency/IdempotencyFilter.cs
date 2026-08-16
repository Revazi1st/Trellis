using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Trellis.Application.Abstractions;

namespace Trellis.Api.Common.Idempotency;

/// <summary>
/// Opt-in command idempotency. When a request carries an <c>Idempotency-Key</c> header, the key is
/// reserved before the handler runs; a duplicate key short-circuits with 409. The reservation is
/// scoped to the caller and the route — different users (or different endpoints) reusing the same
/// header value never collide. A failed outcome — an exception or a 4xx/5xx result (this codebase
/// reports expected failures as Results, ADR-0004) — releases the key so the request can be retried.
/// (Response replay is the evolution.)
/// </summary>
public sealed class IdempotencyFilter : IEndpointFilter
{
    public const string HeaderName = "Idempotency-Key";
    public const int MaxKeyLength = 128;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext http = context.HttpContext;

        if (!http.Request.Headers.TryGetValue(HeaderName, out var header) || string.IsNullOrWhiteSpace(header))
        {
            return await next(context);
        }

        string clientKey = header.ToString();
        if (clientKey.Length > MaxKeyLength)
        {
            return TypedResults.Problem(
                title: "Invalid Idempotency-Key",
                detail: $"The {HeaderName} header must be at most {MaxKeyLength} characters long.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Hashing the (user, route, key) composite gives the stored key a fixed length and keeps the
        // caller's raw value out of the database.
        var currentUser = http.RequestServices.GetRequiredService<ICurrentUser>();
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{currentUser.UserId?.ToString() ?? "anonymous"}:{http.Request.Path}:{clientKey}")));

        var store = http.RequestServices.GetRequiredService<IIdempotencyStore>();

        if (!await store.TryReserveAsync(key, http.RequestAborted))
        {
            return TypedResults.Problem(
                title: "Duplicate request",
                detail: "A request with this Idempotency-Key has already been processed.",
                statusCode: StatusCodes.Status409Conflict);
        }

        try
        {
            object? result = await next(context);

            if (StatusCodeOf(result) is >= 400)
            {
                await store.ReleaseAsync(key, CancellationToken.None);
            }

            return result;
        }
        catch
        {
            await store.ReleaseAsync(key, CancellationToken.None);
            throw;
        }
    }

    private static int? StatusCodeOf(object? result) => result switch
    {
        INestedHttpResult nested => StatusCodeOf(nested.Result),
        IStatusCodeHttpResult { StatusCode: { } code } => code,
        _ => null,
    };
}

/// <summary>Endpoint marker so the OpenAPI transformer can document the header and the 409 outcome.</summary>
public sealed class IdempotencyMetadata
{
    public static readonly IdempotencyMetadata Instance = new();

    private IdempotencyMetadata()
    {
    }
}

public static class IdempotencyExtensions
{
    /// <summary>Applies opt-in idempotency to a state-changing endpoint.</summary>
    public static RouteHandlerBuilder RequireIdempotency(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter<IdempotencyFilter>().WithMetadata(IdempotencyMetadata.Instance);
}
