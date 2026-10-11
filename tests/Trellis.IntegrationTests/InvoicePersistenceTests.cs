using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Application.Features.Invoices;
using Trellis.Application.Features.Invoices.GetInvoice;
using Trellis.Application.Features.Invoices.ListInvoices;
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

    [Fact]
    public async Task List_specification_translates_to_sql_newest_first_and_filters_by_status()
    {
        // One invoice per state, each created an hour after the last: CreatedAtUtc comes from the auditing
        // interceptor's clock, so the creation order is the order the list must answer in, reversed.
        async Task<InvoiceId> CreateAnHourLater(string name)
        {
            Time.Advance(TimeSpan.FromHours(1));
            return await Services.CreateInvoiceAsync(name);
        }

        InvoiceId draft = await CreateAnHourLater("draft");
        InvoiceId issued = await CreateAnHourLater("issued");
        await Services.AddInvoiceLineAsync(issued);
        await Services.IssueInvoiceAsync(issued);
        InvoiceId paid = await CreateAnHourLater("paid");
        await Services.AddInvoiceLineAsync(paid);
        await Services.IssueInvoiceAsync(paid);
        await Services.PayInvoiceAsync(paid);
        InvoiceId voided = await CreateAnHourLater("voided");
        await Services.VoidInvoiceAsync(voided);

        using var readScope = CreateScope();
        var list = readScope.ServiceProvider
            .GetRequiredService<IQueryHandler<ListInvoicesQuery, Trellis.SharedKernel.Specifications.PagedList<InvoiceResponse>>>();
        CancellationToken ct = TestContext.Current.CancellationToken;

        var all = (await list.Handle(new ListInvoicesQuery(), ct)).ShouldBeSuccess();
        all.TotalCount.ShouldBe(4);
        all.Items.Select(static i => i.Id).ShouldBe([voided.Value, paid.Value, issued.Value, draft.Value]);

        var onlyIssued = (await list.Handle(new ListInvoicesQuery(InvoiceStatus.Issued), ct)).ShouldBeSuccess();
        onlyIssued.TotalCount.ShouldBe(1);
        onlyIssued.Items.ShouldHaveSingleItem().Id.ShouldBe(issued.Value);

        var secondPage = (await list.Handle(new ListInvoicesQuery(Page: 2, PageSize: 3), ct)).ShouldBeSuccess();
        secondPage.TotalCount.ShouldBe(4);
        secondPage.Items.ShouldHaveSingleItem().Id.ShouldBe(draft.Value, "the oldest invoice is the last row of the last page");
    }
}
