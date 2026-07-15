using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using XTemplate.Application.Features.Invoices;
using XTemplate.Application.Features.Invoices.AddInvoiceLine;
using XTemplate.Application.Features.Invoices.CreateInvoice;
using XTemplate.Application.Features.Invoices.GetInvoice;
using XTemplate.Application.Features.Invoices.IssueInvoice;
using XTemplate.Application.Features.Invoices.ListOverdueInvoices;
using XTemplate.Application.Features.Invoices.PayInvoice;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;
using XTemplate.SharedKernel.Results;
using Xunit;

namespace XTemplate.IntegrationTests;

public sealed class InvoicePersistenceTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    [Fact]
    public async Task Full_aggregate_roundtrips_through_postgres()
    {
        var create = Services.GetRequiredService<ICommandHandler<CreateInvoiceCommand, InvoiceId>>();
        var addLine = Services.GetRequiredService<ICommandHandler<AddInvoiceLineCommand>>();

        Result<InvoiceId> created = await create.Handle(
            new CreateInvoiceCommand("ACME Corp", "billing@acme.com", "usd"), TestContext.Current.CancellationToken);
        created.IsSuccess.ShouldBeTrue();

        (await addLine.Handle(new AddInvoiceLineCommand(created.Value, "Design", 150.50m), TestContext.Current.CancellationToken))
            .IsSuccess.ShouldBeTrue();
        (await addLine.Handle(new AddInvoiceLineCommand(created.Value, "Build", 349.50m), TestContext.Current.CancellationToken))
            .IsSuccess.ShouldBeTrue();

        using var readScope = CreateScope();
        var get = readScope.ServiceProvider.GetRequiredService<IQueryHandler<GetInvoiceQuery, InvoiceResponse>>();
        Result<InvoiceResponse> fetched = await get.Handle(new GetInvoiceQuery(created.Value), TestContext.Current.CancellationToken);

        fetched.IsSuccess.ShouldBeTrue();
        InvoiceResponse invoice = fetched.Value;
        invoice.CustomerName.ShouldBe("ACME Corp");
        invoice.CustomerEmail.ShouldBe("billing@acme.com");
        invoice.Currency.ShouldBe("USD");
        invoice.Status.ShouldBe("Draft");
        invoice.Total.ShouldBe(500.00m);
        invoice.Lines.Count.ShouldBe(2);
        invoice.Lines.Select(static l => l.Description).ShouldBe(["Design", "Build"], ignoreOrder: true);
    }

    [Fact]
    public async Task Overdue_specification_translates_to_sql_and_orders_by_due_date()
    {
        var create = Services.GetRequiredService<ICommandHandler<CreateInvoiceCommand, InvoiceId>>();
        var addLine = Services.GetRequiredService<ICommandHandler<AddInvoiceLineCommand>>();
        var issue = Services.GetRequiredService<ICommandHandler<IssueInvoiceCommand>>();
        var pay = Services.GetRequiredService<ICommandHandler<PayInvoiceCommand>>();

        async Task<InvoiceId> Seed(string name, DateTimeOffset? dueDate, bool paid = false)
        {
            InvoiceId id = (await create.Handle(new CreateInvoiceCommand(name, "a@b.co", "USD"), TestContext.Current.CancellationToken)).Value;
            await addLine.Handle(new AddInvoiceLineCommand(id, "Line", 10m), TestContext.Current.CancellationToken);
            if (dueDate is { } due)
            {
                (await issue.Handle(new IssueInvoiceCommand(id, due), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
            }
            if (paid)
            {
                (await pay.Handle(new PayInvoiceCommand(id), TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
            }
            return id;
        }

        // Now = 2026-07-20 (fixture clock)
        await Seed("overdue-july", new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        await Seed("overdue-june", new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        await Seed("not-due-yet", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        await Seed("paid", new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero), paid: true);
        await Seed("still-draft", dueDate: null);

        using var readScope = CreateScope();
        var list = readScope.ServiceProvider
            .GetRequiredService<IQueryHandler<ListOverdueInvoicesQuery, XTemplate.SharedKernel.Specifications.PagedList<InvoiceResponse>>>();

        var overdue = await list.Handle(new ListOverdueInvoicesQuery(Page: 1, PageSize: 20), TestContext.Current.CancellationToken);

        overdue.IsSuccess.ShouldBeTrue();
        overdue.Value.TotalCount.ShouldBe(2);
        overdue.Value.Items.Select(static i => i.CustomerName).ShouldBe(["overdue-june", "overdue-july"]);
    }
}
