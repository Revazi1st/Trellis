using Trellis.Domain.Common;
using Trellis.Domain.Invoices;

namespace Trellis.Application.UnitTests.Invoices;

internal static class InvoiceTestData
{
    public static Invoice Draft()
        => Invoice.Create(
            "ACME Corp",
            EmailAddress.Create("billing@acme.com").ShouldBeSuccess(),
            Currency.Create("USD").ShouldBeSuccess()).ShouldBeSuccess();

    public static Invoice Issued()
    {
        Invoice invoice = Draft();
        invoice.AddLine("Consulting", Money.Create(100m, "USD").ShouldBeSuccess()).ShouldBeSuccess();
        invoice.Issue(new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero), now: new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero))
            .ShouldBeSuccess();
        invoice.ClearDomainEvents();
        return invoice;
    }
}
