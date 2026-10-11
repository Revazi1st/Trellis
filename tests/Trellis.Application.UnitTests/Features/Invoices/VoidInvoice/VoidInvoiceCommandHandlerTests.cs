using NSubstitute;
using Shouldly;
using Trellis.Application.Features.Invoices;
using Trellis.Application.Features.Invoices.VoidInvoice;
using Trellis.Domain.Invoices;
using Trellis.Domain.Invoices.Events;
using Trellis.SharedKernel.Results;
using Trellis.TestSupport.Invoices;
using Xunit;

namespace Trellis.Application.UnitTests.Features.Invoices.VoidInvoice;

public sealed class VoidInvoiceCommandHandlerTests
{
    private readonly IInvoiceRepository _invoices = Substitute.For<IInvoiceRepository>();

    [Fact]
    public async Task Unknown_invoice_returns_not_found()
    {
        var missingId = InvoiceId.New();
        _invoices.GetByIdAsync(missingId, Arg.Any<CancellationToken>()).Returns((Invoice?)null);

        Result result = await Handle(new VoidInvoiceCommand(missingId));

        result.Error.ShouldBe(InvoiceErrors.NotFound(missingId));
    }

    [Fact]
    public async Task Voids_a_draft_and_raises_the_voided_event()
    {
        Invoice invoice = Loaded(InvoiceFactory.Draft());

        (await Handle(new VoidInvoiceCommand(invoice.Id))).ShouldBeSuccess();

        invoice.Status.ShouldBe(InvoiceStatus.Voided);
        invoice.DomainEvents.ShouldHaveSingleItem().ShouldBeOfType<InvoiceVoidedDomainEvent>().InvoiceId.ShouldBe(invoice.Id);
    }

    [Fact]
    public async Task A_paid_invoice_cannot_be_voided()
    {
        Invoice invoice = Loaded(InvoiceFactory.Paid());
        invoice.ClearDomainEvents();

        Result result = await Handle(new VoidInvoiceCommand(invoice.Id));

        result.Error.ShouldBe(InvoiceErrors.AlreadyFinalized);
        invoice.Status.ShouldBe(InvoiceStatus.Paid);
        invoice.DomainEvents.ShouldBeEmpty();
    }

    /// <summary>The <c>If-Match</c> path: a version the invoice has moved on from is refused before Void runs.</summary>
    [Fact]
    public async Task A_stale_expected_version_is_a_precondition_failure_that_changes_nothing()
    {
        Invoice invoice = Loaded(InvoiceFactory.Draft());

        Result result = await Handle(new VoidInvoiceCommand(invoice.Id, ExpectedVersion: invoice.Version + 1));

        result.Error.ShouldBe(InvoicePreconditions.VersionMismatch);
        invoice.Status.ShouldBe(InvoiceStatus.Draft);
        invoice.DomainEvents.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_current_expected_version_voids()
    {
        Invoice invoice = Loaded(InvoiceFactory.Draft());

        (await Handle(new VoidInvoiceCommand(invoice.Id, ExpectedVersion: invoice.Version))).ShouldBeSuccess();

        invoice.Status.ShouldBe(InvoiceStatus.Voided);
    }

    [Fact]
    public async Task A_missing_invoice_is_not_found_whatever_version_was_expected()
    {
        // Without the condition the answer would have been a 404, so a stale version never turns it into a 412.
        var missingId = InvoiceId.New();
        _invoices.GetByIdAsync(missingId, Arg.Any<CancellationToken>()).Returns((Invoice?)null);

        Result result = await Handle(new VoidInvoiceCommand(missingId, ExpectedVersion: 7));

        result.Error.ShouldBe(InvoiceErrors.NotFound(missingId));
    }

    private Invoice Loaded(Invoice invoice)
    {
        _invoices.GetByIdAsync(invoice.Id, Arg.Any<CancellationToken>()).Returns(invoice);
        return invoice;
    }

    private Task<Result> Handle(VoidInvoiceCommand command)
        => new VoidInvoiceCommandHandler(_invoices).Handle(command, TestContext.Current.CancellationToken);
}
