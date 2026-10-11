using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Domain;
using Trellis.TestSupport.Invoices;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// <c>UtcDateTimeOffsetConverter</c>: a value with a non-zero offset — what a client sends — is stored as
/// the instant it denotes and read back as UTC, instead of Npgsql rejecting it for <c>timestamptz</c>.
/// Holds for nullable properties and for query parameters too.
/// </summary>
public sealed class UtcConversionTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    private static readonly TimeSpan PlusTwo = TimeSpan.FromHours(2);
    private static readonly TimeSpan PlusFiveThirty = new(5, 30, 0);

    [Fact]
    public async Task An_offset_value_is_stored_as_its_instant_and_read_back_as_utc()
    {
        var dueDate = new DateTimeOffset(2026, 8, 1, 10, 0, 0, PlusTwo);       // 08:00Z
        var paidAt = new DateTimeOffset(2026, 7, 25, 9, 15, 0, PlusFiveThirty); // 03:45Z (nullable column)

        InvoiceId id = await SaveIssuedAndPaidInvoiceAsync(dueDate, paidAt);

        using IServiceScope scope = CreateScope();
        Invoice stored = (await scope.ServiceProvider.GetRequiredService<IInvoiceRepository>()
            .GetByIdAsync(id, TestContext.Current.CancellationToken)).ShouldNotBeNull();

        stored.DueDate.ShouldBe(new DateTimeOffset(2026, 8, 1, 8, 0, 0, TimeSpan.Zero));
        stored.DueDate!.Value.Offset.ShouldBe(TimeSpan.Zero);
        stored.PaidAtUtc.ShouldBe(new DateTimeOffset(2026, 7, 25, 3, 45, 0, TimeSpan.Zero));
        stored.PaidAtUtc!.Value.Offset.ShouldBe(TimeSpan.Zero);
    }

    [Fact]
    public async Task An_offset_value_works_as_a_query_parameter()
    {
        var dueDate = new DateTimeOffset(2026, 8, 1, 10, 0, 0, PlusTwo);
        InvoiceId id = await SaveIssuedInvoiceAsync(dueDate);

        // The same instants seen from +05:30: one second after the due date is overdue, one before is not.
        DateTimeOffset justAfter = (dueDate + TimeSpan.FromSeconds(1)).ToOffset(PlusFiveThirty);
        DateTimeOffset justBefore = (dueDate - TimeSpan.FromSeconds(1)).ToOffset(PlusFiveThirty);

        using IServiceScope scope = CreateScope();
        var invoices = scope.ServiceProvider.GetRequiredService<IInvoiceRepository>();

        (await invoices.ListAsync(InvoiceSpecs.Overdue(justAfter), TestContext.Current.CancellationToken))
            .Select(static invoice => invoice.Id).ShouldBe([id]);
        (await invoices.ListAsync(InvoiceSpecs.Overdue(justBefore), TestContext.Current.CancellationToken))
            .ShouldBeEmpty();
    }

    private async Task<InvoiceId> SaveIssuedInvoiceAsync(DateTimeOffset dueDate)
        => await SaveAsync(dueDate, paidAt: null);

    private async Task<InvoiceId> SaveIssuedAndPaidInvoiceAsync(DateTimeOffset dueDate, DateTimeOffset paidAt)
        => await SaveAsync(dueDate, paidAt);

    private async Task<InvoiceId> SaveAsync(DateTimeOffset dueDate, DateTimeOffset? paidAt)
    {
        // Issued by a clock reading at +02:00 too: only the instants matter.
        DateTimeOffset issuedAt = Time.GetUtcNow().ToOffset(PlusTwo);
        Invoice invoice = paidAt is { } paid
            ? InvoiceFactory.Paid(paid, dueDate, issuedAt)
            : InvoiceFactory.Issued(dueDate, issuedAt);

        using IServiceScope scope = CreateScope();
        scope.ServiceProvider.GetRequiredService<IInvoiceRepository>().Add(invoice);
        (await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(TestContext.Current.CancellationToken))
            .ShouldBeSuccess();
        return invoice.Id;
    }
}
