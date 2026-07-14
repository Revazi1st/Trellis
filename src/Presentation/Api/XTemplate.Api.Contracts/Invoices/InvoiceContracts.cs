namespace XTemplate.Api.Contracts.Invoices;

/// <summary>Wire contracts for the Invoices feature — shared verbatim by the API host, Blazor web and MAUI mobile clients.</summary>
public sealed record CreateInvoiceRequest(string CustomerName, string CustomerEmail, string Currency);

public sealed record CreateInvoiceResponse(Guid Id);

public sealed record AddInvoiceLineRequest(string Description, decimal Amount);

public sealed record IssueInvoiceRequest(DateTimeOffset DueDate);

public sealed record InvoiceDto(
    Guid Id,
    string CustomerName,
    string CustomerEmail,
    string Currency,
    string Status,
    DateTimeOffset? DueDate,
    DateTimeOffset? PaidAtUtc,
    decimal Total,
    IReadOnlyList<InvoiceLineDto> Lines);

public sealed record InvoiceLineDto(Guid Id, string Description, decimal Amount);
