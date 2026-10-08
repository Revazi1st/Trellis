using Microsoft.EntityFrameworkCore;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Specifications;

namespace Trellis.Persistence.Repositories;

internal sealed class InvoiceRepository(AppDbContext context) : IInvoiceRepository
{
    public void Add(Invoice invoice) => context.Invoices.Add(invoice);

    public void Remove(Invoice invoice) => context.Invoices.Remove(invoice);

    public async Task<Invoice?> GetByIdAsync(InvoiceId id, CancellationToken cancellationToken)
        => await context.Invoices.FirstOrDefaultAsync(invoice => invoice.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Invoice>> ListAsync(Specification<Invoice> specification, CancellationToken cancellationToken)
        => await specification.ApplyTo(context.Invoices.AsNoTracking()).ToListAsync(cancellationToken);

    public async Task<PagedList<Invoice>> ListPagedAsync(
        Specification<Invoice> specification, int page, int pageSize, CancellationToken cancellationToken)
    {
        // This method pages; the specification must not, and its order must be total (ends with Id).
        specification.EnsureCanBePaged();

        // The query's validator bounds page and pageSize; these are the programmer-error guards behind it:
        // an unchecked (page - 1) * pageSize wraps negative for a huge page and Postgres rejects a negative
        // OFFSET with an error that reads like a database fault.
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        int skip = checked((page - 1) * pageSize);

        IQueryable<Invoice> source = context.Invoices.AsNoTracking();

        int totalCount = await specification.ApplyCriteria(source).CountAsync(cancellationToken);

        List<Invoice> items = await specification.ApplyTo(source)
            .Skip(skip)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedList<Invoice>(items, page, pageSize, totalCount);
    }
}
