using NSubstitute;
using Shouldly;
using Trellis.Application.Features.Invoices.AddInvoiceLine;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.Application.UnitTests.Invoices;

public sealed class AddInvoiceLineCommandHandlerTests
{
    private readonly IInvoiceRepository _invoices = Substitute.For<IInvoiceRepository>();

    [Fact]
    public async Task Handle_ValidLine_AddsItToTheDraft()
    {
        Invoice invoice = Loaded(InvoiceTestData.Draft());

        var result = await Handle(new AddInvoiceLineCommand(invoice.Id, "Design", 150.50m));

        result.ShouldBeSuccess();
        invoice.Lines.ShouldHaveSingleItem().Amount.Amount.ShouldBe(150.50m);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10.999)]
    [InlineData(1_000_000_000_000_000)]
    public async Task Handle_InvalidAmount_FailsAtTheAmountField(decimal amount)
    {
        Invoice invoice = Loaded(InvoiceTestData.Draft());

        var result = await Handle(new AddInvoiceLineCommand(invoice.Id, "Design", amount));

        result.Error.ShouldBeOfType<FieldError>().Field.ShouldBe(nameof(AddInvoiceLineCommand.Amount));
        result.Error.Code.ShouldStartWith("Money.");
        invoice.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task Handle_BlankDescription_FailsAtTheDescriptionField()
    {
        Invoice invoice = Loaded(InvoiceTestData.Draft());

        var result = await Handle(new AddInvoiceLineCommand(invoice.Id, " ", 10m));

        FieldError error = result.Error.ShouldBeOfType<FieldError>();
        error.Field.ShouldBe(nameof(AddInvoiceLineCommand.Description));
        error.Code.ShouldBe(InvoiceErrors.EmptyLineDescription.Code);
    }

    [Fact]
    public async Task Handle_OverlongDescription_FailsAtTheDescriptionField()
    {
        Invoice invoice = Loaded(InvoiceTestData.Draft());

        var result = await Handle(new AddInvoiceLineCommand(invoice.Id, new string('d', InvoiceLine.DescriptionMaxLength + 1), 10m));

        FieldError error = result.Error.ShouldBeOfType<FieldError>();
        error.Field.ShouldBe(nameof(AddInvoiceLineCommand.Description));
        error.Code.ShouldBe(InvoiceErrors.LineDescriptionTooLong.Code);
    }

    [Fact]
    public async Task Handle_IssuedInvoice_IsAConflictNoFieldOwns()
    {
        Invoice invoice = Loaded(InvoiceTestData.Issued());

        var result = await Handle(new AddInvoiceLineCommand(invoice.Id, "Design", 1m));

        // AddLine's state rule is a 409 about the invoice, not a field: AtField leaves it as it is.
        result.Error.ShouldBe(InvoiceErrors.NotDraft);
    }

    [Fact]
    public async Task Handle_UnknownInvoice_ReturnsNotFound()
    {
        var missingId = InvoiceId.New();
        _invoices.GetByIdAsync(missingId, Arg.Any<CancellationToken>()).Returns((Invoice?)null);

        var result = await Handle(new AddInvoiceLineCommand(missingId, "Design", 1m));

        result.Error.ShouldBe(InvoiceErrors.NotFound(missingId));
    }

    private Invoice Loaded(Invoice invoice)
    {
        _invoices.GetByIdAsync(invoice.Id, Arg.Any<CancellationToken>()).Returns(invoice);
        return invoice;
    }

    private Task<Result> Handle(AddInvoiceLineCommand command)
        => new AddInvoiceLineCommandHandler(_invoices).Handle(command, TestContext.Current.CancellationToken);
}
