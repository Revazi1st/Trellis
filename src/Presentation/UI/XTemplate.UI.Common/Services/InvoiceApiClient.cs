using System.Net;
using System.Net.Http.Json;
using XTemplate.Api.Contracts.Invoices;

namespace XTemplate.UI.Common.Services;

/// <summary>Typed client for the Invoices API; the injected HttpClient carries base address (host) and bearer token (handler).</summary>
internal sealed class InvoiceApiClient(HttpClient httpClient) : IInvoiceApiClient
{
    public async Task<ApiResult<IReadOnlyList<InvoiceDto>>> GetOverdueAsync(CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await httpClient.GetAsync("api/v1/invoices/overdue", cancellationToken);
        return await ReadListAsync(response, cancellationToken);
    }

    public async Task<ApiResult<InvoiceDto>> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await httpClient.GetAsync($"api/v1/invoices/{id}", cancellationToken);
        return await ReadInvoiceAsync(response, cancellationToken);
    }

    public async Task<ApiResult<Guid>> CreateAsync(CreateInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        HttpResponseMessage response = await httpClient.PostAsJsonAsync("api/v1/invoices", request, cancellationToken);
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
        HttpResponseMessage response = body is null
            ? await httpClient.PostAsync(uri, content: null, cancellationToken)
            : await httpClient.PostAsJsonAsync(uri, body, cancellationToken);

        return response.IsSuccessStatusCode
            ? ApiResult.Success(true)
            : ApiResult.Failure<bool>(await DescribeAsync(response, cancellationToken));
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

    private static async Task<ApiResult<IReadOnlyList<InvoiceDto>>> ReadListAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            return ApiResult.Failure<IReadOnlyList<InvoiceDto>>(await DescribeAsync(response, cancellationToken));
        }

        IReadOnlyList<InvoiceDto>? list = await response.Content.ReadFromJsonAsync<IReadOnlyList<InvoiceDto>>(cancellationToken);
        return ApiResult.Success<IReadOnlyList<InvoiceDto>>(list ?? []);
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
