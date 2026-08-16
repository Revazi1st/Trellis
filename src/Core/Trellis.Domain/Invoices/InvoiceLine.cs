using Trellis.Domain.Common;
using Trellis.SharedKernel.Domain;

namespace Trellis.Domain.Invoices;

/// <summary>Child entity of <see cref="Invoice"/>; created only through Invoice.AddLine.</summary>
public sealed class InvoiceLine : Entity<InvoiceLineId>
{
    private InvoiceLine(InvoiceLineId id, string description, Money amount)
        : base(id)
    {
        Description = description;
        Amount = amount;
    }

    /// <summary>EF Core materialization only.</summary>
    private InvoiceLine()
    {
    }

    public string Description { get; private set; } = null!;

    public Money Amount { get; private set; } = null!;

    internal static InvoiceLine Create(string description, Money amount)
        => new(InvoiceLineId.New(), description, amount);
}
