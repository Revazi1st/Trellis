using System.Text.Json.Serialization;

namespace Trellis.Api.Contracts.Invoices;

/// <summary>Wire contracts for the Invoices feature — shared verbatim by the API host and its clients (the Blazor web UI, tests).</summary>
public sealed record CreateInvoiceRequest(string CustomerName, string CustomerEmail, string Currency);

public sealed record CreateInvoiceResponse(Guid Id);

public sealed record AddInvoiceLineRequest(string Description, decimal Amount);

public sealed record IssueInvoiceRequest(DateTimeOffset DueDate);

/// <summary>
/// Lifecycle state of an invoice, serialised by name (<c>"Issued"</c>) so the document declares a string
/// enum and clients get a type instead of a free string. Mirrors the domain's <c>InvoiceStatus</c>
/// member for member; an architecture test keeps the two equal (ADR-0025).
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<InvoiceStatus>))]
public enum InvoiceStatus
{
    Draft = 0,
    Issued = 1,
    Paid = 2,
    Voided = 3,
}

/// <summary>One invoice with its lines. <c>Version</c> is the row version behind the strong <c>ETag</c> of <c>GET /invoices/{id}</c>, which a write may send back as <c>If-Match</c>: opaque, monotonic per invoice.</summary>
public sealed record InvoiceDto(
    Guid Id,
    string CustomerName,
    string CustomerEmail,
    string Currency,
    InvoiceStatus Status,
    DateTimeOffset? DueDate,
    DateTimeOffset? PaidAtUtc,
    decimal Total,
    long Version,
    IReadOnlyList<InvoiceLineDto> Lines);

public sealed record InvoiceLineDto(Guid Id, string Description, decimal Amount);
