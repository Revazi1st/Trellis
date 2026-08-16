using Trellis.SharedKernel.Domain;

namespace Trellis.Domain.Invoices;

public readonly record struct InvoiceLineId(Guid Value) : IStronglyTypedId<InvoiceLineId>, IParsable<InvoiceLineId>
{
    public static InvoiceLineId New() => new(Guid.CreateVersion7());

    public static InvoiceLineId From(Guid value) => new(value);

    public static InvoiceLineId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse(string? s, IFormatProvider? provider, out InvoiceLineId result)
    {
        bool ok = Guid.TryParse(s, provider, out Guid value);
        result = new InvoiceLineId(value);
        return ok;
    }

    public override string ToString() => Value.ToString();
}
