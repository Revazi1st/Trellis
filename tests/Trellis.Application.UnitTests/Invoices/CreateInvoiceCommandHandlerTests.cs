using NSubstitute;
using Shouldly;
using Trellis.Application.Features.Invoices.CreateInvoice;
using Trellis.Domain.Common;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.Application.UnitTests.Invoices;

public sealed class CreateInvoiceCommandHandlerTests
{
    private readonly IInvoiceRepository _invoices = Substitute.For<IInvoiceRepository>();

    [Fact]
    public async Task Valid_command_adds_invoice_and_returns_its_id()
    {
        var handler = new CreateInvoiceCommandHandler(_invoices);

        var result = await handler.Handle(new CreateInvoiceCommand("ACME", "a@b.co", "usd"), TestContext.Current.CancellationToken);

        result.IsSuccess.ShouldBeTrue();
        result.Value.Value.ShouldNotBe(Guid.Empty);
        _invoices.Received(1).Add(Arg.Is<Invoice>(i => i != null && i.Id == result.Value && i.Currency == "USD"));
    }

    [Fact]
    public async Task Invalid_email_fails_at_its_field_without_touching_repository()
    {
        var handler = new CreateInvoiceCommandHandler(_invoices);

        var result = await handler.Handle(new CreateInvoiceCommand("ACME", "a@b", "USD"), TestContext.Current.CancellationToken);

        FieldError error = result.Error.ShouldBeOfType<FieldError>();
        error.Field.ShouldBe(nameof(CreateInvoiceCommand.CustomerEmail));
        error.Code.ShouldBe(EmailAddressErrors.Invalid.Code);
        _invoices.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Fact]
    public async Task Every_invalid_value_is_reported_at_once_each_at_its_field()
    {
        var handler = new CreateInvoiceCommandHandler(_invoices);

        var result = await handler.Handle(new CreateInvoiceCommand("ACME", "not-an-email", "12$"), TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<ValidationError>().Errors
            .Select(static e => (e.ShouldBeOfType<FieldError>().Field, e.Code))
            .ShouldBe([
                (nameof(CreateInvoiceCommand.CustomerEmail), EmailAddressErrors.Invalid.Code),
                (nameof(CreateInvoiceCommand.Currency), MoneyErrors.InvalidCurrency.Code),
            ]);
    }

    [Fact]
    public async Task A_blank_customer_name_fails_at_its_field()
    {
        var handler = new CreateInvoiceCommandHandler(_invoices);

        var result = await handler.Handle(new CreateInvoiceCommand(" ", "a@b.co", "USD"), TestContext.Current.CancellationToken);

        FieldError error = result.Error.ShouldBeOfType<FieldError>();
        error.Field.ShouldBe(nameof(CreateInvoiceCommand.CustomerName));
        error.Code.ShouldBe(InvoiceErrors.EmptyCustomerName.Code);
    }
}
