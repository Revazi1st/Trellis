using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using XTemplate.Application.Features.Invoices.AddInvoiceLine;
using XTemplate.Application.Features.Invoices.CreateInvoice;
using XTemplate.Application.Features.Invoices.IssueInvoice;
using XTemplate.Application.Features.Invoices.PayInvoice;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;
using Xunit;

namespace XTemplate.IntegrationTests;

public sealed class DomainEventDispatchTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    [Fact]
    public async Task Paying_an_invoice_dispatches_the_event_and_sends_the_receipt()
    {
        var create = Services.GetRequiredService<ICommandHandler<CreateInvoiceCommand, InvoiceId>>();
        var addLine = Services.GetRequiredService<ICommandHandler<AddInvoiceLineCommand>>();
        var issue = Services.GetRequiredService<ICommandHandler<IssueInvoiceCommand>>();

        InvoiceId id = (await create.Handle(
            new CreateInvoiceCommand("ACME", "billing@acme.com", "USD"), TestContext.Current.CancellationToken)).Value;
        await addLine.Handle(new AddInvoiceLineCommand(id, "Line", 10m), TestContext.Current.CancellationToken);
        (await issue.Handle(new IssueInvoiceCommand(id, Now.AddDays(14)), TestContext.Current.CancellationToken))
            .IsSuccess.ShouldBeTrue();

        // Issue raised InvoiceIssuedDomainEvent (no handler registered) — the dispatcher must tolerate that.
        await EmailSender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, TestContext.Current.CancellationToken);

        using var payScope = CreateScope();
        var pay = payScope.ServiceProvider.GetRequiredService<ICommandHandler<PayInvoiceCommand>>();
        (await pay.Handle(new PayInvoiceCommand(id), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        await EmailSender.Received(1).SendAsync(
            "billing@acme.com",
            Arg.Any<string>(),
            Arg.Is<string>(body => body != null && body.Contains(id.ToString())),
            Arg.Any<CancellationToken>());
    }
}
