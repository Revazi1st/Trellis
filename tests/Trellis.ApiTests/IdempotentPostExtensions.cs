using System.Net.Http.Json;
using Trellis.Api.Contracts.Common;

namespace Trellis.ApiTests;

/// <summary>
/// A POST with a fresh <c>Idempotency-Key</c>, the way the UI's typed client sends every write. For the
/// arrange steps of tests that are not about the key: <c>POST /invoices</c> declares
/// <c>RequireIdempotency(keyRequired: true)</c> and answers a request without one with a 400
/// (<c>Idempotency.KeyRequired</c>). Tests about the key itself build their request by hand.
/// </summary>
internal static class IdempotentPostExtensions
{
    public static async Task<HttpResponseMessage> PostWithIdempotencyKeyAsync<TValue>(
        this HttpClient client, string requestUri, TValue value, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri) { Content = JsonContent.Create(value) };
        request.Headers.Add(ApiHeaders.IdempotencyKey, Guid.CreateVersion7().ToString());
        return await client.SendAsync(request, cancellationToken);
    }
}
