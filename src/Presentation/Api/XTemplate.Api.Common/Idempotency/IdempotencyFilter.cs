using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using XTemplate.Application.Abstractions;

namespace XTemplate.Api.Common.Idempotency;

/// <summary>
/// Opt-in command idempotency. When a request carries an <c>Idempotency-Key</c> header, the key is
/// reserved before the handler runs; a duplicate key short-circuits with 409. If the handler throws,
/// the key is released so a genuinely-failed request can be retried. (Response replay is the evolution.)
/// </summary>
public sealed class IdempotencyFilter : IEndpointFilter
{
    public const string HeaderName = "Idempotency-Key";

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext http = context.HttpContext;

        if (!http.Request.Headers.TryGetValue(HeaderName, out var header) || string.IsNullOrWhiteSpace(header))
        {
            return await next(context);
        }

        string key = header.ToString();
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
            return await next(context);
        }
        catch
        {
            await store.ReleaseAsync(key, CancellationToken.None);
            throw;
        }
    }
}

public static class IdempotencyExtensions
{
    /// <summary>Applies opt-in idempotency to a state-changing endpoint.</summary>
    public static RouteHandlerBuilder RequireIdempotency(this RouteHandlerBuilder builder)
        => builder.AddEndpointFilter<IdempotencyFilter>();
}
