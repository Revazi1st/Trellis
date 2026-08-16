using Trellis.SharedKernel.Specifications;

namespace Trellis.Domain.Invoices;

/// <summary>All specifications for the Invoice aggregate — one static factory per query shape.</summary>
public static class InvoiceSpecs
{
    public static Specification<Invoice> Overdue(DateTimeOffset now) => new OverdueSpec(now);

    private sealed class OverdueSpec : Specification<Invoice>
    {
        public OverdueSpec(DateTimeOffset now)
        {
            Where(invoice => invoice.Status == InvoiceStatus.Issued && invoice.DueDate < now);
            OrderBy(invoice => invoice.DueDate);
        }
    }
}
