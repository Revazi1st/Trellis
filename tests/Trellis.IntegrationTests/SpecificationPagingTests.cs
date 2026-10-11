using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Specifications;
using Trellis.TestSupport.Invoices;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// Paging over a specification is only correct when the order is total: <c>InvoiceSpecs.Overdue</c> ends
/// with <c>Id</c>, so invoices due at the same instant still page without repeats or gaps, and
/// <c>ListPagedAsync</c> refuses a specification that pages itself or has no unique tiebreaker.
/// </summary>
public sealed class SpecificationPagingTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    private const int InvoiceCount = 7;

    [Fact]
    public void The_overdue_specification_ends_with_the_id_tiebreaker()
        => InvoiceSpecs.Overdue(Now).EndsWithIdTiebreaker.ShouldBeTrue();

    [Fact]
    public async Task Invoices_due_at_the_same_instant_page_in_id_order_without_repeats_or_gaps()
    {
        DateTimeOffset dueDate = Now.AddDays(1);
        Invoice[] invoices = [.. Enumerable.Range(0, InvoiceCount).Select(i => IssuedInvoice($"Tie {i}", dueDate))];

        // Inserted in reverse id order, so the table's physical order is not already the expected one.
        using (IServiceScope scope = CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IInvoiceRepository>();
            foreach (Invoice invoice in invoices.Reverse())
            {
                repository.Add(invoice);
                (await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(TestContext.Current.CancellationToken))
                    .ShouldBeSuccess();
            }
        }

        DateTimeOffset afterDue = dueDate.AddDays(1);
        var paged = new List<InvoiceId>();
        for (int page = 1; page <= InvoiceCount; page++)
        {
            using IServiceScope scope = CreateScope();
            PagedList<Invoice> result = await scope.ServiceProvider.GetRequiredService<IInvoiceRepository>()
                .ListPagedAsync(InvoiceSpecs.Overdue(afterDue), page, pageSize: 1, TestContext.Current.CancellationToken);

            result.TotalCount.ShouldBe(InvoiceCount);
            paged.AddRange(result.Items.Select(static invoice => invoice.Id));
        }

        // PostgreSQL orders uuid by its bytes, which is the ordinal order of the canonical string.
        paged.ShouldBe([.. invoices
            .Select(static invoice => invoice.Id)
            .OrderBy(static id => id.Value.ToString("D"), StringComparer.Ordinal)]);
    }

    [Fact]
    public async Task Paging_a_specification_that_already_pages_itself_is_refused()
    {
        var invoices = Services.GetRequiredService<IInvoiceRepository>();

        InvalidOperationException refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => invoices.ListPagedAsync(new FirstThreeById(), page: 1, pageSize: 10, TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain("already pages itself");
    }

    [Fact]
    public async Task Paging_a_specification_without_the_id_tiebreaker_is_refused()
    {
        var invoices = Services.GetRequiredService<IInvoiceRepository>();

        InvalidOperationException refusal = await Should.ThrowAsync<InvalidOperationException>(
            () => invoices.ListPagedAsync(new ByDueDateOnly(), page: 1, pageSize: 10, TestContext.Current.CancellationToken));

        refusal.Message.ShouldContain("OrderBy(x => x.Id)");
    }

    [Fact]
    public async Task A_self_paging_specification_still_lists()
    {
        IReadOnlyList<Invoice> listed = await Services.GetRequiredService<IInvoiceRepository>()
            .ListAsync(new FirstThreeById(), TestContext.Current.CancellationToken);

        listed.ShouldBeEmpty();
    }

    private Invoice IssuedInvoice(string customer, DateTimeOffset dueDate)
        => InvoiceFactory.Issued(dueDate, issuedAt: Time.GetUtcNow(), customerName: customer);

    private sealed class FirstThreeById : Specification<Invoice>
    {
        public FirstThreeById()
        {
            OrderBy(invoice => invoice.Id);
            Paginate(skip: 0, take: 3);
        }
    }

    private sealed class ByDueDateOnly : Specification<Invoice>
    {
        public ByDueDateOnly() => OrderBy(invoice => invoice.DueDate);
    }
}
