using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Application.Features.Invoices;
using Trellis.Application.Features.Invoices.GetInvoice;
using Trellis.Application.Features.Invoices.ListOverdueInvoices;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.IntegrationTests;

public sealed class InvoicePersistenceTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    [Fact]
    public async Task Full_aggregate_roundtrips_through_postgres()
    {
        // A lower-case currency: stored as the normalised code.
        InvoiceId id = await Services.CreateInvoiceAsync("ACME Corp", "billing@acme.com", currency: "usd");
        await Services.AddInvoiceLineAsync(id, "Design", 150.50m);
        await Services.AddInvoiceLineAsync(id, "Build", 349.50m);

        using var readScope = CreateScope();
        var get = readScope.ServiceProvider.GetRequiredService<IQueryHandler<GetInvoiceQuery, InvoiceResponse>>();
        Result<InvoiceResponse> fetched = await get.Handle(new GetInvoiceQuery(id), TestContext.Current.CancellationToken);

        InvoiceResponse invoice = fetched.ShouldBeSuccess();
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
        async Task Seed(string name, DateTimeOffset? dueDate, bool paid = false)
        {
            InvoiceId id = await Services.CreateInvoiceAsync(name);
            await Services.AddInvoiceLineAsync(id);
            if (dueDate is { } due)
            {
                await Services.IssueInvoiceAsync(id, due);
            }

            if (paid)
            {
                await Services.PayInvoiceAsync(id);
            }
        }

        // Issued at the fixture clock's 2026-07-20 (a due date is never past when issued)...
        await Seed("overdue-september", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero));
        await Seed("overdue-august", new DateTimeOffset(2026, 8, 1, 0, 0, 0, TimeSpan.Zero));
        await Seed("not-due-yet", new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero));
        await Seed("paid", new DateTimeOffset(2026, 8, 15, 0, 0, 0, TimeSpan.Zero), paid: true);
        await Seed("still-draft", dueDate: null);

        // ...and listed on 2026-10-01, when two of them have fallen due.
        Time.SetUtcNow(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));

        using var readScope = CreateScope();
        var list = readScope.ServiceProvider
            .GetRequiredService<IQueryHandler<ListOverdueInvoicesQuery, Trellis.SharedKernel.Specifications.PagedList<InvoiceResponse>>>();

        var overdue = await list.Handle(new ListOverdueInvoicesQuery(Page: 1, PageSize: 20), TestContext.Current.CancellationToken);

        overdue.ShouldBeSuccess().TotalCount.ShouldBe(2);
        overdue.Value.Items.Select(static i => i.CustomerName).ShouldBe(["overdue-august", "overdue-september"]);
    }
}
