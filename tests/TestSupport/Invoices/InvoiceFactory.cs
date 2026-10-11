using Trellis.Application.Features.Invoices.AddInvoiceLine;
using Trellis.Application.Features.Invoices.CreateInvoice;
using Trellis.Application.Features.Invoices.IssueInvoice;
using Trellis.Domain.Common;
using Trellis.Domain.Invoices;

namespace Trellis.TestSupport.Invoices;

/// <summary>
/// Valid Invoice commands and aggregates for arrange steps. Every parameter has a valid default, so a test
/// names only what it is about, and a change to <c>Invoice.Create</c> or a command's shape lands here
/// instead of in every test. A value the test depends on (the customer it later looks up, the due date it
/// compares, the line it counts) is passed at the call site, never left to a default. The aggregates check
/// every step with <c>ShouldBeSuccess()</c> and keep the events their transitions raised (an outbox test
/// saves them); call <c>ClearDomainEvents()</c> for one that looks freshly loaded.
/// One source file, compiled into Application.UnitTests and IntegrationTests (each csproj links it).
/// </summary>
internal static class InvoiceFactory
{
    public const string Customer = "ACME Corp";
    public const string Email = "billing@acme.com";
    public const string CurrencyCode = "USD";
    public const string LineDescription = "Consulting";
    public const decimal LineAmount = 100m;

    /// <summary>The clock <see cref="Issued"/> issues by unless told otherwise.</summary>
    public static readonly DateTimeOffset IssuedAt = new(2026, 7, 15, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The due date <see cref="Issued"/> sets unless told otherwise, after <see cref="IssuedAt"/>.</summary>
    public static readonly DateTimeOffset DueDate = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>When <see cref="Paid"/> pays unless told otherwise.</summary>
    public static readonly DateTimeOffset PaidAt = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    public static CreateInvoiceCommand CreateCommand(
        string customerName = Customer, string customerEmail = Email, string currency = CurrencyCode)
        => new(customerName, customerEmail, currency);

    public static AddInvoiceLineCommand AddLineCommand(
        InvoiceId invoiceId, string description = LineDescription, decimal amount = LineAmount, uint? expectedVersion = null)
        => new(invoiceId, description, amount, expectedVersion);

    /// <summary>The due date has no default: whether it is in the future depends on the clock the handler reads.</summary>
    public static IssueInvoiceCommand IssueCommand(InvoiceId invoiceId, DateTimeOffset dueDate, uint? expectedVersion = null)
        => new(invoiceId, dueDate, expectedVersion);

    public static Invoice Draft(string customerName = Customer, string customerEmail = Email, string currency = CurrencyCode)
        => Invoice.Create(
            customerName,
            EmailAddress.Create(customerEmail).ShouldBeSuccess(),
            Currency.Create(currency).ShouldBeSuccess()).ShouldBeSuccess();

    public static Invoice DraftWithLine(
        string description = LineDescription, decimal amount = LineAmount, string customerName = Customer)
    {
        Invoice invoice = Draft(customerName);
        invoice.AddLine(description, Money.Create(amount, invoice.Currency).ShouldBeSuccess()).ShouldBeSuccess();
        return invoice;
    }

    public static Invoice Issued(DateTimeOffset? dueDate = null, DateTimeOffset? issuedAt = null, string customerName = Customer)
    {
        Invoice invoice = DraftWithLine(customerName: customerName);
        invoice.Issue(dueDate ?? DueDate, issuedAt ?? IssuedAt).ShouldBeSuccess();
        return invoice;
    }

    public static Invoice Paid(
        DateTimeOffset? paidAt = null, DateTimeOffset? dueDate = null, DateTimeOffset? issuedAt = null, string customerName = Customer)
    {
        Invoice invoice = Issued(dueDate, issuedAt, customerName);
        invoice.MarkPaid(paidAt ?? PaidAt).ShouldBeSuccess();
        return invoice;
    }
}
