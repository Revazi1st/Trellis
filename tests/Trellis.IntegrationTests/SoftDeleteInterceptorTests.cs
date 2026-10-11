using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Domain.Invoices;
using Trellis.Persistence;
using Trellis.SharedKernel.Domain;
using Xunit;

namespace Trellis.IntegrationTests;

public sealed class SoftDeleteInterceptorTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    [Fact]
    public async Task Remove_becomes_soft_delete_and_preserves_child_lines()
    {
        InvoiceId id = await Services.CreateInvoiceAsync();
        await Services.AddInvoiceLineAsync(id, "Design");
        await Services.AddInvoiceLineAsync(id, "Build");

        using (var deleteScope = CreateScope())
        {
            var repository = deleteScope.ServiceProvider.GetRequiredService<IInvoiceRepository>();
            Invoice invoice = (await repository.GetByIdAsync(id, TestContext.Current.CancellationToken))!;
            repository.Remove(invoice);
            (await deleteScope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(TestContext.Current.CancellationToken)).ShouldBeSuccess();
        }

        using var readScope = CreateScope();
        AppDbContext context = readScope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Filtered out of normal queries…
        (await context.Invoices.SingleOrDefaultAsync(i => i.Id == id, TestContext.Current.CancellationToken)).ShouldBeNull();

        // …but the row survives with the deletion stamp — and its children stay intact.
        Invoice? deleted = await context.Invoices
            .IgnoreQueryFilters(["SoftDeleted"])
            .SingleOrDefaultAsync(i => i.Id == id, TestContext.Current.CancellationToken);

        deleted.ShouldNotBeNull();
        deleted.DeletedAtUtc.ShouldBe(Now);
        deleted.Lines.Count.ShouldBe(2);
        (await context.Set<InvoiceLine>().CountAsync(TestContext.Current.CancellationToken)).ShouldBe(2);
    }
}
