using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Specifications;

namespace Trellis.Application.Features.Invoices.ListOverdueInvoices;

public sealed record ListOverdueInvoicesQuery(int Page = 1, int PageSize = 20)
    : IQuery<PagedList<InvoiceResponse>>;
