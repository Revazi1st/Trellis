using NSubstitute;
using Trellis.Application.Abstractions;
using Trellis.Application.Features.Invoices.EventHandlers;
using Trellis.Domain.Invoices;
using Trellis.Domain.Invoices.Events;
using Xunit;

namespace Trellis.Application.UnitTests.Invoices;

public sealed class SendReceiptOnInvoicePaidTests
{
    [Fact]
    public async Task Sends_receipt_to_the_customer_email()
    {
        var emailSender = Substitute.For<IEmailSender>();
        var handler = new SendReceiptOnInvoicePaid(emailSender);
        var invoiceId = InvoiceId.New();

        await handler.Handle(
            new InvoicePaidDomainEvent(invoiceId, "billing@acme.com"),
            TestContext.Current.CancellationToken);

        await emailSender.Received(1).SendAsync(
            "billing@acme.com",
            Arg.Any<string>(),
            Arg.Is<string>(body => body != null && body.Contains(invoiceId.ToString())),
            Arg.Any<CancellationToken>());
    }
}
