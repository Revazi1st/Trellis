using NSubstitute;
using Shouldly;
using Trellis.Application.Features.Invoices.GetInvoice;
using Trellis.Domain.Invoices;
using Trellis.TestSupport.Invoices;
using Xunit;

namespace Trellis.Application.UnitTests.Invoices;

public sealed class GetInvoiceQueryHandlerTests
{
    private readonly IInvoiceRepository _invoices = Substitute.For<IInvoiceRepository>();

    [Fact]
    public async Task Maps_aggregate_to_response()
    {
        Invoice invoice = InvoiceFactory.DraftWithLine("Design", 150.50m);
        _invoices.GetByIdAsync(invoice.Id, Arg.Any<CancellationToken>()).Returns(invoice);
        var handler = new GetInvoiceQueryHandler(_invoices);

        var result = await handler.Handle(new GetInvoiceQuery(invoice.Id), TestContext.Current.CancellationToken);

        result.ShouldBeSuccess();
        result.Value.Id.ShouldBe(invoice.Id.Value);
        result.Value.CustomerEmail.ShouldBe("billing@acme.com");
        result.Value.Status.ShouldBe("Draft");
        result.Value.Total.ShouldBe(150.50m);
        result.Value.Lines.ShouldHaveSingleItem().Description.ShouldBe("Design");
    }

    [Fact]
    public async Task Unknown_invoice_returns_not_found()
    {
        var missingId = InvoiceId.New();
        _invoices.GetByIdAsync(missingId, Arg.Any<CancellationToken>()).Returns((Invoice?)null);
        var handler = new GetInvoiceQueryHandler(_invoices);

        var result = await handler.Handle(new GetInvoiceQuery(missingId), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(InvoiceErrors.NotFound(missingId));
    }
}
