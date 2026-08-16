using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Application.Features.Invoices.CreateInvoice;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Messaging;
using Xunit;

namespace Trellis.IntegrationTests;

public sealed class ConcurrencyTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    [Fact]
    public async Task Concurrent_updates_to_the_same_invoice_raise_a_concurrency_conflict()
    {
        // Arrange: one persisted invoice.
        InvoiceId id = (await Services.GetRequiredService<ICommandHandler<CreateInvoiceCommand, InvoiceId>>()
            .Handle(new CreateInvoiceCommand("ACME", "a@b.co", "USD"), TestContext.Current.CancellationToken)).Value;

        // Two independent scopes load the same aggregate (each with its own xmin snapshot).
        using var scopeA = CreateScope();
        using var scopeB = CreateScope();
        var repoA = scopeA.ServiceProvider.GetRequiredService<IInvoiceRepository>();
        var repoB = scopeB.ServiceProvider.GetRequiredService<IInvoiceRepository>();

        Invoice invoiceA = (await repoA.GetByIdAsync(id, TestContext.Current.CancellationToken))!;
        Invoice invoiceB = (await repoB.GetByIdAsync(id, TestContext.Current.CancellationToken))!;

        invoiceA.AddLine("From A", Trellis.Domain.Common.Money.Create(10m, "USD").Value);
        invoiceB.AddLine("From B", Trellis.Domain.Common.Money.Create(20m, "USD").Value);

        // Act: A commits first; B's xmin is now stale.
        await scopeA.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken);

        // Assert: B's commit is rejected by the xmin concurrency token.
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() =>
            scopeB.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync(TestContext.Current.CancellationToken));
    }
}
