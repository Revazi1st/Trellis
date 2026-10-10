using System.Net.Http.Json;
using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;

namespace Trellis.UI.Common.Services;

/// <summary>
/// Typed client for the Invoices API; the injected HttpClient carries base address (host) and bearer token (handler).
/// Every call goes through <see cref="ApiCall"/>: failures carry the status and the API's problem, described by
/// <see cref="ApiResponses"/>, and an unreachable API is a result, not an exception.
/// </summary>
internal sealed class InvoiceApiClient(HttpClient httpClient) : IInvoiceApiClient
{
    public Task<ApiResult<PagedResponse<InvoiceDto>>> GetOverdueAsync(
        int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
        => ApiCall.ReadAsync<PagedResponse<InvoiceDto>>(
            ct => httpClient.GetAsync($"api/v1/invoices/overdue?page={page}&pageSize={pageSize}", ct), cancellationToken);

    public Task<ApiResult<InvoiceDto>> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => ApiCall.ReadAsync<InvoiceDto>(ct => httpClient.GetAsync($"api/v1/invoices/{id}", ct), cancellationToken);

    public Task<ApiResult<Guid>> CreateAsync(CreateInvoiceRequest request, CancellationToken cancellationToken = default)
        => ApiCall.ReadAsync<CreateInvoiceResponse, Guid>(
            ct => PostWithIdempotencyAsync("api/v1/invoices", request, ct),
            static created => created.Id,
            cancellationToken);

    public Task<ApiResult<bool>> AddLineAsync(Guid id, AddInvoiceLineRequest request, CancellationToken cancellationToken = default)
        => ApiCall.SendAsync(ct => PostWithIdempotencyAsync($"api/v1/invoices/{id}/lines", request, ct), cancellationToken);

    public Task<ApiResult<bool>> IssueAsync(Guid id, IssueInvoiceRequest request, CancellationToken cancellationToken = default)
        => ApiCall.SendAsync(ct => PostWithIdempotencyAsync($"api/v1/invoices/{id}/issue", request, ct), cancellationToken);

    public Task<ApiResult<bool>> PayAsync(Guid id, CancellationToken cancellationToken = default)
        => ApiCall.SendAsync(ct => PostWithIdempotencyAsync<object>($"api/v1/invoices/{id}/pay", body: null, ct), cancellationToken);

    public Task<ApiResult<bool>> VoidAsync(Guid id, CancellationToken cancellationToken = default)
        => ApiCall.SendAsync(ct => PostWithIdempotencyAsync<object>($"api/v1/invoices/{id}/void", body: null, ct), cancellationToken);

    /// <summary>
    /// Every POST carries a fresh Idempotency-Key, set once per logical call. Nothing re-sends a POST
    /// today: ServiceDefaults' blanket resilience handler retries only safe methods
    /// (<c>DisableForUnsafeHttpMethods</c>), so a POST whose response is lost is reported, not repeated.
    /// The key is what makes a repeat harmless should one ever happen (a client given its own retrying
    /// handler, ADR-0020; a proxy that retries): every attempt shares it, the API commits the request
    /// once and answers a duplicate 409 <c>Idempotency.Duplicate</c>.
    /// </summary>
    private async Task<HttpResponseMessage> PostWithIdempotencyAsync<TBody>(
        string uri, TBody? body, CancellationToken cancellationToken)
        where TBody : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add(ApiHeaders.IdempotencyKey, Guid.CreateVersion7().ToString());
        return await httpClient.SendAsync(request, cancellationToken);
    }
}
