using NSubstitute;
using Shouldly;
using Trellis.Application.Features.Invoices;
using Trellis.Application.Features.Invoices.ListInvoices;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Specifications;
using Trellis.TestSupport.Invoices;
using Xunit;

namespace Trellis.Application.UnitTests.Features.Invoices.ListInvoices;

public sealed class ListInvoicesQueryHandlerTests
{
    private static readonly DateTimeOffset FirstCreated = new(2026, 7, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly IInvoiceRepository _invoices = Substitute.For<IInvoiceRepository>();

    [Fact]
    public async Task Maps_the_page_and_keeps_its_paging_metadata()
    {
        Invoice first = InvoiceFactory.Draft(customerName: "Acme");
        Invoice second = InvoiceFactory.Issued(customerName: "Globex");
        _invoices.ListPagedAsync(Arg.Any<Specification<Invoice>>(), 3, 10, Arg.Any<CancellationToken>())
            .Returns(new PagedList<Invoice>([first, second], Page: 3, PageSize: 10, TotalCount: 42));
        var handler = new ListInvoicesQueryHandler(_invoices);

        var result = await handler.Handle(new ListInvoicesQuery(Page: 3, PageSize: 10), TestContext.Current.CancellationToken);

        PagedList<InvoiceResponse> page = result.ShouldBeSuccess();
        page.Items.Select(static invoice => invoice.CustomerName).ShouldBe(["Acme", "Globex"]);
        page.Items.Select(static invoice => invoice.Status).ShouldBe(["Draft", "Issued"]);
        page.Items[0].Id.ShouldBe(first.Id.Value);
        page.Page.ShouldBe(3);
        page.PageSize.ShouldBe(10);
        page.TotalCount.ShouldBe(42);
        page.TotalPages.ShouldBe(5);
        page.HasNextPage.ShouldBeTrue();
        page.HasPreviousPage.ShouldBeTrue();
    }

    [Fact]
    public async Task Without_a_status_it_asks_for_every_invoice_newest_first()
    {
        (Invoice draft, Invoice issued, Invoice paid, Invoice voided) = OneInEachState();

        Specification<Invoice> asked = await AskedSpecificationAsync(new ListInvoicesQuery());

        asked.Evaluate([issued, voided, draft, paid]).ShouldBe([voided, paid, issued, draft]);
    }

    [Theory]
    [InlineData(InvoiceStatus.Draft)]
    [InlineData(InvoiceStatus.Issued)]
    [InlineData(InvoiceStatus.Paid)]
    [InlineData(InvoiceStatus.Voided)]
    public async Task With_a_status_it_asks_for_the_invoices_in_that_state_only(InvoiceStatus status)
    {
        (Invoice draft, Invoice issued, Invoice paid, Invoice voided) = OneInEachState();

        Specification<Invoice> asked = await AskedSpecificationAsync(new ListInvoicesQuery(status));

        asked.Evaluate([issued, voided, draft, paid]).ShouldHaveSingleItem().Status.ShouldBe(status);
    }

    [Fact]
    public async Task The_specification_it_asks_for_can_be_paged()
    {
        Specification<Invoice> asked = await AskedSpecificationAsync(new ListInvoicesQuery(InvoiceStatus.Issued));

        asked.EndsWithIdTiebreaker.ShouldBeTrue();
        asked.IsPaged.ShouldBeFalse("the repository pages it");
    }

    /// <summary>
    /// One invoice per state, each created a day after the one before. Distinct creation times: the in-memory
    /// evaluation cannot order <see cref="InvoiceId"/> (PostgreSQL orders the uuid, and IntegrationTests prove that).
    /// </summary>
    private static (Invoice Draft, Invoice Issued, Invoice Paid, Invoice Voided) OneInEachState()
    {
        Invoice draft = InvoiceFactory.Draft(customerName: "Draft Co");
        Invoice issued = InvoiceFactory.Issued(customerName: "Issued Co");
        Invoice paid = InvoiceFactory.Paid(customerName: "Paid Co");
        Invoice voided = InvoiceFactory.Voided(customerName: "Voided Co");
        draft.CreatedAtUtc = FirstCreated;
        issued.CreatedAtUtc = FirstCreated.AddDays(1);
        paid.CreatedAtUtc = FirstCreated.AddDays(2);
        voided.CreatedAtUtc = FirstCreated.AddDays(3);
        return (draft, issued, paid, voided);
    }

    private async Task<Specification<Invoice>> AskedSpecificationAsync(ListInvoicesQuery query)
    {
        Specification<Invoice>? asked = null;
        _invoices.ListPagedAsync(Arg.Do<Specification<Invoice>>(spec => asked = spec), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PagedList<Invoice>([], Page: query.Page, PageSize: query.PageSize, TotalCount: 0));
        var handler = new ListInvoicesQueryHandler(_invoices);

        (await handler.Handle(query, TestContext.Current.CancellationToken)).ShouldBeSuccess();

        return asked.ShouldNotBeNull();
    }
}
