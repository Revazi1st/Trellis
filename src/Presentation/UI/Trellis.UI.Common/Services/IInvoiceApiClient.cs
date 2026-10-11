using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;

namespace Trellis.UI.Common.Services;

public interface IInvoiceApiClient
{
    /// <summary>Every invoice, newest first; <paramref name="status"/> narrows the list to one state.</summary>
    Task<ApiResult<PagedResponse<InvoiceDto>>> ListAsync(
        InvoiceStatus? status = null, int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);

    Task<ApiResult<PagedResponse<InvoiceDto>>> GetOverdueAsync(
        int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);

    Task<ApiResult<InvoiceDto>> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ApiResult<Guid>> CreateAsync(CreateInvoiceRequest request, CancellationToken cancellationToken = default);

    // The writes to an existing invoice take the version the page loaded (InvoiceDto.Version) and send it
    // as If-Match, so a change someone made since is a 412 instead of a write on a state never shown.

    Task<ApiResult<bool>> AddLineAsync(Guid id, AddInvoiceLineRequest request, long? expectedVersion = null, CancellationToken cancellationToken = default);

    Task<ApiResult<bool>> IssueAsync(Guid id, IssueInvoiceRequest request, long? expectedVersion = null, CancellationToken cancellationToken = default);

    Task<ApiResult<bool>> PayAsync(Guid id, long? expectedVersion = null, CancellationToken cancellationToken = default);

    Task<ApiResult<bool>> VoidAsync(Guid id, long? expectedVersion = null, CancellationToken cancellationToken = default);
}
