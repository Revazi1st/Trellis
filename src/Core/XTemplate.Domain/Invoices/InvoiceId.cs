using XTemplate.SharedKernel.Domain;

namespace XTemplate.Domain.Invoices;

public readonly record struct InvoiceId(Guid Value) : IStronglyTypedId<InvoiceId>, IParsable<InvoiceId>
{
    public static InvoiceId New() => new(Guid.CreateVersion7());

    public static InvoiceId From(Guid value) => new(value);

    public static InvoiceId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    public static bool TryParse(string? s, IFormatProvider? provider, out InvoiceId result)
    {
        bool ok = Guid.TryParse(s, provider, out Guid value);
        result = new InvoiceId(value);
        return ok;
    }

    public override string ToString() => Value.ToString();
}
