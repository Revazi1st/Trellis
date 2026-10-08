using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Trellis.Api.Common.Idempotency;

/// <summary>
/// What makes two requests "the same request" for one <c>Idempotency-Key</c>: method, path (query
/// string included) and body, hashed to 64 hex characters. The body is the one the endpoint bound,
/// re-serialised with the host's JSON options: an endpoint filter runs after binding, when the request
/// stream is already spent, and the canonical form is the better definition anyway — a retry that
/// differs only in whitespace or member order is still the same request, one that changes a value is not.
/// </summary>
internal static class RequestFingerprint
{
    public static string Of(EndpointFilterInvocationContext context)
    {
        HttpRequest request = context.HttpContext.Request;
        string bodyHash = Convert.ToHexString(SHA256.HashData(BoundBodyOf(context)));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{request.Method}\n{request.PathBase}{request.Path}{request.QueryString}\n{bodyHash}")));
    }

    /// <summary>
    /// The body argument, located through the endpoint's own declaration of its request type
    /// (<see cref="IAcceptsMetadata"/>, which minimal APIs add for every bound body); empty for an endpoint
    /// that takes none.
    /// </summary>
    private static byte[] BoundBodyOf(EndpointFilterInvocationContext context)
    {
        Type? bodyType = context.HttpContext.GetEndpoint()?.Metadata.GetMetadata<IAcceptsMetadata>()?.RequestType;
        object? body = bodyType is null
            ? null
            : context.Arguments.FirstOrDefault(argument => argument is not null && bodyType.IsInstanceOfType(argument));

        if (body is null)
        {
            return [];
        }

        JsonSerializerOptions options = context.HttpContext.RequestServices
            .GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        return JsonSerializer.SerializeToUtf8Bytes(body, bodyType!, options);
    }
}
