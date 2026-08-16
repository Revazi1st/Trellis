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
        IQueryable<Invoice> source = context.Invoices.AsNoTracking();

        int totalCount = await specification.ApplyCriteria(source).CountAsync(cancellationToken);

        List<Invoice> items = await specification.ApplyTo(source)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedList<Invoice>(items, page, pageSize, totalCount);
    }
}
