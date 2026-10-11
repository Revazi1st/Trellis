using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Application.Features.Invoices;
using Trellis.Application.Features.Invoices.ListOverdueInvoices;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Specifications;
using Xunit;

namespace Trellis.IntegrationTests;

public sealed class PaginationTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    [Fact]
    public async Task Overdue_list_is_paged_with_a_correct_total_count()
    {
        // Seed 5 invoices due a day after the fixture clock (a due date is never past when issued)...
        for (int i = 0; i < 5; i++)
        {
            await Services.IssuedInvoiceAsync(dueDate: Now.AddDays(1), customerName: $"Cust {i}");
        }

        // ...then let the clock run past it: all five are overdue.
        Time.Advance(TimeSpan.FromDays(2));

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
        result.Error.Type.ShouldBe(Trellis.SharedKernel.Results.ErrorType.Validation);
    }
}
