using System.Net.Http.Json;
using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;

namespace Trellis.UI.Common.Services;

/// <summary>
/// Typed client for the Invoices API; the injected HttpClient carries base address (host) and bearer token (handler).
/// Failures carry the status and the API's problem, described by <see cref="ApiResponses"/>.
/// </summary>
internal sealed class InvoiceApiClient(HttpClient httpClient) : IInvoiceApiClient
{
    public async Task<ApiResult<PagedResponse<InvoiceDto>>> GetOverdueAsync(
        int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage? response = await ApiResponses.TrySendAsync(
            ct => httpClient.GetAsync($"api/v1/invoices/overdue?page={page}&pageSize={pageSize}", ct), cancellationToken);

        if (response is not { IsSuccessStatusCode: true })
        {
            return await ApiResponses.FailureFromAsync<PagedResponse<InvoiceDto>>(response, cancellationToken);
        }

        PagedResponse<InvoiceDto>? result = await response.Content.ReadFromJsonAsync<PagedResponse<InvoiceDto>>(cancellationToken);
        return ApiResult.Success(result!);
    }

    public async Task<ApiResult<InvoiceDto>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage? response = await ApiResponses.TrySendAsync(
            ct => httpClient.GetAsync($"api/v1/invoices/{id}", ct), cancellationToken);

        if (response is not { IsSuccessStatusCode: true })
        {
            return await ApiResponses.FailureFromAsync<InvoiceDto>(response, cancellationToken);
        }

        InvoiceDto? dto = await response.Content.ReadFromJsonAsync<InvoiceDto>(cancellationToken);
        return ApiResult.Success(dto!);
    }

    public async Task<ApiResult<Guid>> CreateAsync(CreateInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        using HttpResponseMessage? response = await PostWithIdempotencyAsync("api/v1/invoices", request, cancellationToken);

        if (response is not { IsSuccessStatusCode: true })
        {
            return await ApiResponses.FailureFromAsync<Guid>(response, cancellationToken);
        }

        CreateInvoiceResponse? created = await response.Content.ReadFromJsonAsync<CreateInvoiceResponse>(cancellationToken);
        return ApiResult.Success(created!.Id);
    }

    public Task<ApiResult<bool>> AddLineAsync(Guid id, AddInvoiceLineRequest request, CancellationToken cancellationToken = default)
        => PostAsync($"api/v1/invoices/{id}/lines", request, cancellationToken);

    public Task<ApiResult<bool>> IssueAsync(Guid id, IssueInvoiceRequest request, CancellationToken cancellationToken = default)
        => PostAsync($"api/v1/invoices/{id}/issue", request, cancellationToken);

    public Task<ApiResult<bool>> PayAsync(Guid id, CancellationToken cancellationToken = default)
        => PostAsync<object?>($"api/v1/invoices/{id}/pay", null, cancellationToken);

    public Task<ApiResult<bool>> VoidAsync(Guid id, CancellationToken cancellationToken = default)
        => PostAsync<object?>($"api/v1/invoices/{id}/void", null, cancellationToken);

    private async Task<ApiResult<bool>> PostAsync<TBody>(string uri, TBody body, CancellationToken cancellationToken)
    {
        using HttpResponseMessage? response = await PostWithIdempotencyAsync(uri, body, cancellationToken);

        return response is { IsSuccessStatusCode: true }
            ? ApiResult.Success(true)
            : await ApiResponses.FailureFromAsync<bool>(response, cancellationToken);
    }

    /// <summary>
    /// Every POST carries a fresh Idempotency-Key: the standard resilience handler auto-retries
    /// transient failures, and without the key a retry of an already-committed request would commit
    /// it twice. The key is set once per logical call, so all retry attempts share it.
    /// Null when no response arrived (<see cref="ApiResponses.TrySendAsync"/>).
    /// </summary>
    private async Task<HttpResponseMessage?> PostWithIdempotencyAsync<TBody>(
        string uri, TBody? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add(ApiHeaders.IdempotencyKey, Guid.CreateVersion7().ToString());
        return await ApiResponses.TrySendAsync(ct => httpClient.SendAsync(request, ct), cancellationToken);
    }
}
