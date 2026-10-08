using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Application.Features.Invoices.AddInvoiceLine;
using Trellis.Application.Features.Invoices.CreateInvoice;
using Trellis.Domain.Invoices;
using Trellis.Persistence;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Messaging;
using Xunit;

namespace Trellis.IntegrationTests;

public sealed class SoftDeleteInterceptorTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    [Fact]
    public async Task Remove_becomes_soft_delete_and_preserves_child_lines()
    {
        var create = Services.GetRequiredService<ICommandHandler<CreateInvoiceCommand, InvoiceId>>();
        var addLine = Services.GetRequiredService<ICommandHandler<AddInvoiceLineCommand>>();

        InvoiceId id = (await create.Handle(
            new CreateInvoiceCommand("ACME", "a@b.co", "USD"), TestContext.Current.CancellationToken)).Value;
        await addLine.Handle(new AddInvoiceLineCommand(id, "Design", 10m), TestContext.Current.CancellationToken);
        await addLine.Handle(new AddInvoiceLineCommand(id, "Build", 20m), TestContext.Current.CancellationToken);

        using (var deleteScope = CreateScope())
        {
            var repository = deleteScope.ServiceProvider.GetRequiredService<IInvoiceRepository>();
            Invoice invoice = (await repository.GetByIdAsync(id, TestContext.Current.CancellationToken))!;
            repository.Remove(invoice);
            (await deleteScope.ServiceProvider.GetRequiredService<IUnitOfWork>()
                .CommitAsync(TestContext.Current.CancellationToken)).IsSuccess.ShouldBeTrue();
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
