using XTemplate.Domain.Invoices;

namespace XTemplate.Application.Features.Invoices;

/// <summary>Application-level read model for an invoice; endpoints map it onto Api.Contracts types.</summary>
public sealed record InvoiceResponse(
    Guid Id,
    string CustomerName,
    string CustomerEmail,
    string Currency,
    string Status,
    DateTimeOffset? DueDate,
    DateTimeOffset? PaidAtUtc,
    decimal Total,
    uint Version,
    IReadOnlyList<InvoiceLineResponse> Lines);

public sealed record InvoiceLineResponse(Guid Id, string Description, decimal Amount);

public static class InvoiceMappings
{
    public static InvoiceResponse ToResponse(this Invoice invoice)
        => new(
            invoice.Id.Value,
            invoice.CustomerName,
            invoice.CustomerEmail.Value,
            invoice.Currency,
            invoice.Status.ToString(),
            invoice.DueDate,
            invoice.PaidAtUtc,
            invoice.Total.Amount,
            invoice.Version,
            [.. invoice.Lines.Select(static line => new InvoiceLineResponse(line.Id.Value, line.Description, line.Amount.Amount))]);
}
