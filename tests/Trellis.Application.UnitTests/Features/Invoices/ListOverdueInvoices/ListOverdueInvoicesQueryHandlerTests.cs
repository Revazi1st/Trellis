using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Trellis.Application.Features.Invoices;
using Trellis.Application.Features.Invoices.ListOverdueInvoices;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Specifications;
using Trellis.TestSupport.Invoices;
using Xunit;

namespace Trellis.Application.UnitTests.Features.Invoices.ListOverdueInvoices;

public sealed class ListOverdueInvoicesQueryHandlerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private readonly IInvoiceRepository _invoices = Substitute.For<IInvoiceRepository>();
    private readonly FakeTimeProvider _clock = new(Now);

    [Fact]
    public async Task Maps_the_page_and_keeps_its_paging_metadata()
    {
        Invoice first = InvoiceFactory.Issued(customerName: "Acme");
        Invoice second = InvoiceFactory.Issued(customerName: "Globex");
        _invoices.ListPagedAsync(Arg.Any<Specification<Invoice>>(), 2, 10, Arg.Any<CancellationToken>())
            .Returns(new PagedList<Invoice>([first, second], Page: 2, PageSize: 10, TotalCount: 42));
        var handler = new ListOverdueInvoicesQueryHandler(_invoices, _clock);

        var result = await handler.Handle(new ListOverdueInvoicesQuery(Page: 2, PageSize: 10), TestContext.Current.CancellationToken);

        PagedList<InvoiceResponse> page = result.ShouldBeSuccess();
        page.Items.Select(static invoice => invoice.CustomerName).ShouldBe(["Acme", "Globex"]);
        page.Items[0].Id.ShouldBe(first.Id.Value);
        page.Items[0].Status.ShouldBe("Issued");
        page.Page.ShouldBe(2);
        page.PageSize.ShouldBe(10);
        page.TotalCount.ShouldBe(42);
        page.TotalPages.ShouldBe(5);
        page.HasNextPage.ShouldBeTrue();
    }

    [Fact]
    public async Task Asks_for_the_invoices_overdue_at_the_clocks_now()
    {
        Specification<Invoice>? asked = null;
        _invoices.ListPagedAsync(Arg.Do<Specification<Invoice>>(spec => asked = spec), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PagedList<Invoice>([], Page: 1, PageSize: 20, TotalCount: 0));
        var handler = new ListOverdueInvoicesQueryHandler(_invoices, _clock);

        await handler.Handle(new ListOverdueInvoicesQuery(), TestContext.Current.CancellationToken);

        Invoice overdue = InvoiceFactory.Issued(dueDate: Now.AddDays(-1));
        Invoice notYetDue = InvoiceFactory.Issued(dueDate: Now.AddDays(1));
        Invoice paidLate = InvoiceFactory.Paid(paidAt: Now.AddDays(-1), dueDate: Now.AddDays(-2));
        asked.ShouldNotBeNull().Evaluate([notYetDue, paidLate, overdue]).ShouldBe([overdue]);
    }
}
