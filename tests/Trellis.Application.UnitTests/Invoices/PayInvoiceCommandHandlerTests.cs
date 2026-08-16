using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Trellis.Application.Features.Invoices.PayInvoice;
using Trellis.Domain.Invoices;
using Xunit;

namespace Trellis.Application.UnitTests.Invoices;

public sealed class PayInvoiceCommandHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly IInvoiceRepository _invoices = Substitute.For<IInvoiceRepository>();
    private readonly FakeTimeProvider _time = new(Now);

    [Fact]
    public async Task Pays_issued_invoice_stamping_current_time()
    {
        Invoice invoice = InvoiceTestData.Issued();
        _invoices.GetByIdAsync(invoice.Id, Arg.Any<CancellationToken>()).Returns(invoice);
        var handler = new PayInvoiceCommandHandler(_invoices, _time);

        var result = await handler.Handle(new PayInvoiceCommand(invoice.Id), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        invoice.Status.ShouldBe(InvoiceStatus.Paid);
        invoice.PaidAtUtc.ShouldBe(Now);
    }

    [Fact]
    public async Task Unknown_invoice_returns_not_found()
    {
        var missingId = InvoiceId.New();
        _invoices.GetByIdAsync(missingId, Arg.Any<CancellationToken>()).Returns((Invoice?)null);
        var handler = new PayInvoiceCommandHandler(_invoices, _time);

        var result = await handler.Handle(new PayInvoiceCommand(missingId), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(InvoiceErrors.NotFound(missingId));
    }

    [Fact]
    public async Task Draft_invoice_cannot_be_paid()
    {
        Invoice invoice = InvoiceTestData.Draft();
        _invoices.GetByIdAsync(invoice.Id, Arg.Any<CancellationToken>()).Returns(invoice);
        var handler = new PayInvoiceCommandHandler(_invoices, _time);

        var result = await handler.Handle(new PayInvoiceCommand(invoice.Id), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(InvoiceErrors.NotIssued);
    }
}
