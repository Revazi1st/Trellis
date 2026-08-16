using Trellis.Api.Contracts.Common;
using Trellis.Api.Contracts.Invoices;

namespace Trellis.UI.Common.Services;

public interface IInvoiceApiClient
{
    Task<ApiResult<PagedResponse<InvoiceDto>>> GetOverdueAsync(
        int page = 1, int pageSize = 20, CancellationToken cancellationToken = default);

    Task<ApiResult<InvoiceDto>> GetAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ApiResult<Guid>> CreateAsync(CreateInvoiceRequest request, CancellationToken cancellationToken = default);

    Task<ApiResult<bool>> AddLineAsync(Guid id, AddInvoiceLineRequest request, CancellationToken cancellationToken = default);

    Task<ApiResult<bool>> IssueAsync(Guid id, IssueInvoiceRequest request, CancellationToken cancellationToken = default);

    Task<ApiResult<bool>> PayAsync(Guid id, CancellationToken cancellationToken = default);

    Task<ApiResult<bool>> VoidAsync(Guid id, CancellationToken cancellationToken = default);
}
