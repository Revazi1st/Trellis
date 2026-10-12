using Trellis.SharedKernel.Specifications;

namespace Trellis.Domain.Invoices;

/// <summary>All specifications for the Invoice aggregate — one static factory per query shape.</summary>
public static class InvoiceSpecs
{
    /// <summary>Every invoice, or only those in <paramref name="status"/>, newest first.</summary>
    public static Specification<Invoice> All(InvoiceStatus? status = null) => new AllSpec(status);

    public static Specification<Invoice> Overdue(DateTimeOffset now) => new OverdueSpec(now);

    private sealed class AllSpec : Specification<Invoice>
    {
        public AllSpec(InvoiceStatus? status)
        {
            if (status is { } wanted)
            {
                Where(invoice => invoice.Status == wanted);
            }

            OrderByDescending(invoice => invoice.CreatedAtUtc);
            // The unique tiebreaker, descending too: version 7 ids grow with time, so invoices stamped at the
            // same instant (one SaveChanges) still list newest first, and they page stably.
            OrderByDescending(invoice => invoice.Id);
        }
    }

    private sealed class OverdueSpec : Specification<Invoice>
    {
        public OverdueSpec(DateTimeOffset now)
        {
            Where(invoice => invoice.Status == InvoiceStatus.Issued && invoice.DueDate < now);
            OrderBy(invoice => invoice.DueDate);
            OrderBy(invoice => invoice.Id);   // unique tiebreaker: invoices due at the same instant page stably
        }
    }
}
