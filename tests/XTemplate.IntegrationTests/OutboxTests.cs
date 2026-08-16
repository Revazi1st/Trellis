using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using XTemplate.Application.Features.Invoices.AddInvoiceLine;
using XTemplate.Application.Features.Invoices.CreateInvoice;
using XTemplate.Application.Features.Invoices.IssueInvoice;
using XTemplate.Application.Features.Invoices.PayInvoice;
using XTemplate.Domain.Invoices;
using XTemplate.Persistence;
using XTemplate.Persistence.Outbox;
using XTemplate.SharedKernel.Messaging;
using Xunit;

namespace XTemplate.IntegrationTests;

public sealed class OutboxTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    [Fact]
    public async Task Paying_writes_the_event_to_the_outbox_in_the_same_transaction_without_sending_synchronously()
    {
        InvoiceId id = await IssueInvoiceAsync();

        using var payScope = CreateScope();
        (await payScope.ServiceProvider.GetRequiredService<ICommandHandler<PayInvoiceCommand>>()
            .Handle(new PayInvoiceCommand(id), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();

        // The receipt email is NOT sent inside the transaction…
        await EmailSender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, Arg.Any<CancellationToken>());

        // …instead the event sits in the outbox, committed atomically with the payment.
        using var readScope = CreateScope();
        AppDbContext context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        List<OutboxMessage> pending = await context.Set<OutboxMessage>()
            .Where(m => m.ProcessedAtUtc == null)
            .ToListAsync(TestContext.Current.CancellationToken);

        // Both raised events are captured (InvoiceIssued has no email handler; InvoicePaid triggers
        // the receipt). Rows carry the stable wire name, never the CLR type.
        pending.ShouldContain(m => m.Type == "invoices.paid.v1");
        pending.ShouldContain(m => m.Type == "invoices.issued.v1");
        pending.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Outbox_processor_dispatches_the_event_and_sends_the_receipt()
    {
        InvoiceId id = await IssueInvoiceAsync();
        using (var payScope = CreateScope())
        {
            await payScope.ServiceProvider.GetRequiredService<ICommandHandler<PayInvoiceCommand>>()
                .Handle(new PayInvoiceCommand(id), TestContext.Current.CancellationToken);
        }

        var processor = new OutboxProcessor(
            Services.GetRequiredService<IServiceScopeFactory>(), Time,
            new OutboxMetrics(new TestMeterFactory()), NullLogger<OutboxProcessor>.Instance);
        await processor.ProcessBatchAsync(TestContext.Current.CancellationToken);

        await EmailSender.Received(1).SendAsync(
            "billing@acme.com", Arg.Any<string>(),
            Arg.Is<string>(body => body != null && body.Contains(id.ToString())), Arg.Any<CancellationToken>());

        using var readScope = CreateScope();
        AppDbContext context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();
        (await context.Set<OutboxMessage>().CountAsync(m => m.ProcessedAtUtc == null, TestContext.Current.CancellationToken))
            .ShouldBe(0);
    }

    private async Task<InvoiceId> IssueInvoiceAsync()
    {
        InvoiceId id = (await Services.GetRequiredService<ICommandHandler<CreateInvoiceCommand, InvoiceId>>()
            .Handle(new CreateInvoiceCommand("ACME", "billing@acme.com", "USD"), TestContext.Current.CancellationToken)).Value;
        await Services.GetRequiredService<ICommandHandler<AddInvoiceLineCommand>>()
            .Handle(new AddInvoiceLineCommand(id, "Line", 10m), TestContext.Current.CancellationToken);
        await Services.GetRequiredService<ICommandHandler<IssueInvoiceCommand>>()
            .Handle(new IssueInvoiceCommand(id, Now.AddDays(14)), TestContext.Current.CancellationToken);
        return id;
    }

    internal sealed class TestMeterFactory : System.Diagnostics.Metrics.IMeterFactory
    {
        public System.Diagnostics.Metrics.Meter Create(System.Diagnostics.Metrics.MeterOptions options) => new(options);

        public void Dispose()
        {
            // Meters are owned by their consumers in tests.
        }
    }
}
