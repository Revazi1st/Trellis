using XTemplate.Domain.Common;
using XTemplate.Domain.Invoices;

namespace XTemplate.Application.UnitTests.Invoices;

internal static class InvoiceTestData
{
    public static Invoice Draft()
        => Invoice.Create("ACME Corp", EmailAddress.Create("billing@acme.com").Value, Currency.Create("USD").Value).Value;

    public static Invoice Issued()
    {
        Invoice invoice = Draft();
        invoice.AddLine("Consulting", Money.Create(100m, "USD").Value);
        invoice.Issue(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        invoice.ClearDomainEvents();
        return invoice;
    }
}
