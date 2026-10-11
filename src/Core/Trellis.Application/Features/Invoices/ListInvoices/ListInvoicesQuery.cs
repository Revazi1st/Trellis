using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Specifications;

namespace Trellis.Application.Features.Invoices.ListInvoices;

/// <summary>Every invoice, newest first; <paramref name="Status"/> narrows the list to one state.</summary>
public sealed record ListInvoicesQuery(InvoiceStatus? Status = null, int Page = 1, int PageSize = 20)
    : IQuery<PagedList<InvoiceResponse>>;
