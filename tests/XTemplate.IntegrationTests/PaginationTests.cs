using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using XTemplate.Application.Features.Invoices;
using XTemplate.Application.Features.Invoices.AddInvoiceLine;
using XTemplate.Application.Features.Invoices.CreateInvoice;
using XTemplate.Application.Features.Invoices.IssueInvoice;
using XTemplate.Application.Features.Invoices.ListOverdueInvoices;
using XTemplate.Domain.Invoices;
using XTemplate.SharedKernel.Messaging;
using XTemplate.SharedKernel.Specifications;
using Xunit;

namespace XTemplate.IntegrationTests;

public sealed class PaginationTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    [Fact]
    public async Task Overdue_list_is_paged_with_a_correct_total_count()
    {
        var create = Services.GetRequiredService<ICommandHandler<CreateInvoiceCommand, InvoiceId>>();
        var addLine = Services.GetRequiredService<ICommandHandler<AddInvoiceLineCommand>>();
        var issue = Services.GetRequiredService<ICommandHandler<IssueInvoiceCommand>>();

        // Seed 5 overdue invoices (due before the fixture clock's 2026-07-20).
        for (int i = 0; i < 5; i++)
        {
            InvoiceId id = (await create.Handle(new CreateInvoiceCommand($"Cust {i}", "a@b.co", "USD"), TestContext.Current.CancellationToken)).Value;
            await addLine.Handle(new AddInvoiceLineCommand(id, "Line", 10m), TestContext.Current.CancellationToken);
            await issue.Handle(new IssueInvoiceCommand(id, new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)), TestContext.Current.CancellationToken);
        }

        using var scope = CreateScope();
        var list = scope.ServiceProvider.GetRequiredService<IQueryHandler<ListOverdueInvoicesQuery, PagedList<InvoiceResponse>>>();

        var page1 = (await list.Handle(new ListOverdueInvoicesQuery(Page: 1, PageSize: 2), TestContext.Current.CancellationToken)).Value;
        page1.TotalCount.ShouldBe(5);
        page1.Items.Count.ShouldBe(2);
        page1.TotalPages.ShouldBe(3);
        page1.HasNextPage.ShouldBeTrue();
        page1.HasPreviousPage.ShouldBeFalse();

        var page3 = (await list.Handle(new ListOverdueInvoicesQuery(Page: 3, PageSize: 2), TestContext.Current.CancellationToken)).Value;
        page3.Items.Count.ShouldBe(1);
        page3.HasNextPage.ShouldBeFalse();
        page3.HasPreviousPage.ShouldBeTrue();
    }

    [Fact]
    public async Task Invalid_paging_is_rejected_by_the_validator()
    {
        var list = Services.GetRequiredService<IQueryHandler<ListOverdueInvoicesQuery, PagedList<InvoiceResponse>>>();

        var result = await list.Handle(new ListOverdueInvoicesQuery(Page: 0, PageSize: 500), TestContext.Current.CancellationToken);

        result.IsFailure.ShouldBeTrue();
        result.Error.Type.ShouldBe(XTemplate.SharedKernel.Results.ErrorType.Validation);
    }
}
