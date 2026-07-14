using NSubstitute;
using Shouldly;
using XTemplate.Application.Features.Invoices.CreateInvoice;
using XTemplate.Domain.Common;
using XTemplate.Domain.Invoices;
using Xunit;

namespace XTemplate.Application.UnitTests.Invoices;

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
    public async Task Invalid_email_fails_without_touching_repository()
    {
        var handler = new CreateInvoiceCommandHandler(_invoices);

        var result = await handler.Handle(new CreateInvoiceCommand("ACME", "not-an-email", "USD"), TestContext.Current.CancellationToken);

        result.Error.ShouldBe(EmailAddressErrors.Invalid);
        _invoices.DidNotReceiveWithAnyArgs().Add(default!);
    }
}
