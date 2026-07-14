using XTemplate.SharedKernel.Specifications;

namespace XTemplate.Domain.Invoices;

/// <summary>Port implemented by Persistence; aggregates only ever load and save whole.</summary>
public interface IInvoiceRepository
{
    void Add(Invoice invoice);

    void Remove(Invoice invoice);

    Task<Invoice?> GetByIdAsync(InvoiceId id, CancellationToken cancellationToken);

    Task<IReadOnlyList<Invoice>> ListAsync(Specification<Invoice> specification, CancellationToken cancellationToken);
}
