using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Application.Features.Invoices.CreateInvoice;
using Trellis.Domain.Common;
using Trellis.Domain.Invoices;
using Trellis.Persistence;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.IntegrationTests;

public sealed class ConcurrencyTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    [Fact]
    public async Task Concurrent_updates_to_the_same_invoice_raise_a_concurrency_conflict()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        InvoiceId id = await CreateInvoiceAsync(ct);

        // Two independent scopes load the same aggregate (each with its own xmin snapshot).
        using IServiceScope scopeA = CreateScope();
        using IServiceScope scopeB = CreateScope();
        await AddLineAsync(scopeA, id, "From A", 10m, ct);
        await AddLineAsync(scopeB, id, "From B", 20m, ct);

        // Act: A commits first; B's xmin is now stale.
        await scopeA.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync(ct);

        // Assert: the raw EF save rejects B by the xmin concurrency token. This is the behavior
        // IUnitOfWork.CommitAsync translates — kept visible so a provider upgrade cannot change it silently.
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() =>
            scopeB.ServiceProvider.GetRequiredService<AppDbContext>().SaveChangesAsync(ct));
    }

    [Fact]
    public async Task A_stale_commit_through_the_unit_of_work_is_a_Conflict_result_not_an_exception()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        InvoiceId id = await CreateInvoiceAsync(ct);

        using IServiceScope scopeA = CreateScope();
        using IServiceScope scopeB = CreateScope();
        await AddLineAsync(scopeA, id, "From A", 10m, ct);
        await AddLineAsync(scopeB, id, "From B", 20m, ct);

        Result committedA = await scopeA.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(ct);
        Result committedB = await scopeB.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(ct);

        committedA.IsSuccess.ShouldBeTrue();
        committedB.Error.ShouldBe(UnitOfWorkErrors.ConcurrencyConflict);
        committedB.Error.Type.ShouldBe(ErrorType.Conflict);

        // The rejected entries are dropped, so the stale change cannot fail the next commit in this scope.
        scopeB.ServiceProvider.GetRequiredService<AppDbContext>().ChangeTracker.Entries().ShouldBeEmpty();
        (await scopeB.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(ct)).IsSuccess.ShouldBeTrue();

        // Only A's line reached the database.
        using IServiceScope readScope = CreateScope();
        Invoice stored = (await readScope.ServiceProvider.GetRequiredService<IInvoiceRepository>().GetByIdAsync(id, ct))!;
        stored.Lines.ShouldHaveSingleItem().Description.ShouldBe("From A");
    }

    private async Task<InvoiceId> CreateInvoiceAsync(CancellationToken ct)
        => (await Services.GetRequiredService<ICommandHandler<CreateInvoiceCommand, InvoiceId>>()
            .Handle(new CreateInvoiceCommand("ACME", "a@b.co", "USD"), ct)).Value;

    private static async Task AddLineAsync(IServiceScope scope, InvoiceId id, string description, decimal amount, CancellationToken ct)
    {
        Invoice invoice = (await scope.ServiceProvider.GetRequiredService<IInvoiceRepository>().GetByIdAsync(id, ct))!;
        invoice.AddLine(description, Money.Create(amount, "USD").Value).IsSuccess.ShouldBeTrue();
    }
}
