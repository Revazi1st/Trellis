using System.Net;
using System.Net.Http.Json;
using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;

namespace Trellis.UI.Common.Services;

/// <summary>Typed client for the Invoices API; the injected HttpClient carries base address (host) and bearer token (handler).</summary>
internal sealed class InvoiceApiClient(HttpClient httpClient) : IInvoiceApiClient
{
    public async Task<ApiResult<PagedResponse<InvoiceDto>>> GetOverdueAsync(
        int page = 1, int pageSize = 20, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await httpClient.GetAsync(
            $"api/v1/invoices/overdue?page={page}&pageSize={pageSize}", cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            return ApiResult.Failure<PagedResponse<InvoiceDto>>(await DescribeAsync(response, cancellationToken));
        }

        PagedResponse<InvoiceDto>? result = await response.Content.ReadFromJsonAsync<PagedResponse<InvoiceDto>>(cancellationToken);
        return ApiResult.Success(result!);
    }

    public async Task<ApiResult<InvoiceDto>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await httpClient.GetAsync($"api/v1/invoices/{id}", cancellationToken);
        return await ReadInvoiceAsync(response, cancellationToken);
    }

    public async Task<ApiResult<Guid>> CreateAsync(CreateInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await PostWithIdempotencyAsync("api/v1/invoices", request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return ApiResult.Failure<Guid>(await DescribeAsync(response, cancellationToken));
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
        HttpResponseMessage response = await PostWithIdempotencyAsync(uri, body, cancellationToken);

        return response.IsSuccessStatusCode
            ? ApiResult.Success(true)
            : ApiResult.Failure<bool>(await DescribeAsync(response, cancellationToken));
    }

    /// <summary>
    /// Every POST carries a fresh Idempotency-Key: the standard resilience handler auto-retries
    /// transient failures, and without the key a retry of an already-committed request would commit
    /// it twice. The key is set once per logical call, so all retry attempts share it.
    /// </summary>
    private async Task<HttpResponseMessage> PostWithIdempotencyAsync<TBody>(
        string uri, TBody? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, uri);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add(ApiHeaders.IdempotencyKey, Guid.CreateVersion7().ToString());
        return await httpClient.SendAsync(request, cancellationToken);
    }

    private static async Task<ApiResult<InvoiceDto>> ReadInvoiceAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return ApiResult.Failure<InvoiceDto>(await DescribeAsync(response, cancellationToken));
        }

        InvoiceDto? dto = await response.Content.ReadFromJsonAsync<InvoiceDto>(cancellationToken);
        return ApiResult.Success(dto!);
    }

    private static async Task<string> DescribeAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string reason = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "You are not signed in.",
            HttpStatusCode.Forbidden => "You do not have permission to do that.",
            _ => $"Request failed ({(int)response.StatusCode}).",
        };

        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemDetailsLite>(cancellationToken);
            return problem?.Detail ?? reason;
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException or System.Text.Json.JsonException)
        {
            return reason;
        }
    }

    private sealed record ProblemDetailsLite(string? Title, string? Detail);
}
