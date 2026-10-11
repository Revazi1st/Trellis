using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Application.Features.Invoices.CreateInvoice;
using Trellis.Domain.Common;
using Trellis.Domain.Invoices;
using Trellis.Persistence;
using Trellis.Persistence.Interceptors;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Messaging;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// Optimistic concurrency guards the whole aggregate, by convention (ADR-0006): every aggregate root
/// carries a concurrency token, and a change to its children alone still writes the root row, so the
/// token is checked and bumped. The behaviour tests use a context without the auditing interceptor —
/// whose stamping of IAuditable roots used to be the only thing that wrote the root — to prove the
/// promotion pass stands on its own.
/// </summary>
public sealed class AggregateConcurrencyTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    [Fact]
    public void Every_aggregate_root_has_a_concurrency_token_and_a_column_to_promote()
    {
        IModel model = Services.GetRequiredService<AppDbContext>().Model;

        List<IEntityType> aggregateRoots = [.. model.GetEntityTypes()
            .Where(static type => typeof(IHasDomainEvents).IsAssignableFrom(type.ClrType))];

        aggregateRoots.ShouldNotBeEmpty();
        foreach (IEntityType root in aggregateRoots)
        {
            root.GetProperties().Any(static property => property.IsConcurrencyToken).ShouldBeTrue(
                $"{root.DisplayName()} needs a concurrency token (map xmin as its configuration does for Invoice): without one, concurrent edits of the aggregate both commit");
            AggregateConcurrencyInterceptor.PromotableProperty(root).ShouldNotBeNull(
                $"{root.DisplayName()} needs a plain column the promotion pass can write back when only a child changes");
        }
    }

    [Fact]
    public void Every_context_instance_runs_the_promotion_pass()
    {
        using AppDbContext context = UnauditedContext();

        CoreOptionsExtension core = context.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>().ShouldNotBeNull();
        core.Interceptors.ShouldNotBeNull().ShouldContain(AggregateConcurrencyInterceptor.Instance);
    }

    [Fact]
    public async Task A_child_only_change_checks_and_bumps_the_root_version()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        InvoiceId id = await CreateInvoiceAsync(ct);

        await using AppDbContext first = UnauditedContext();
        await using AppDbContext second = UnauditedContext();
        Invoice loadedByFirst = await first.Invoices.SingleAsync(invoice => invoice.Id == id, ct);
        Invoice loadedBySecond = await second.Invoices.SingleAsync(invoice => invoice.Id == id, ct);
        uint versionBefore = loadedByFirst.Version;

        loadedByFirst.AddLine("From the first", Money.Create(10m, "USD").Value).IsSuccess.ShouldBeTrue();
        loadedBySecond.AddLine("From the second", Money.Create(20m, "USD").Value).IsSuccess.ShouldBeTrue();
        await first.SaveChangesAsync(ct);

        loadedByFirst.Version.ShouldNotBe(versionBefore, "adding a line is a change to the invoice");
        await Should.ThrowAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(ct));
    }

    [Fact]
    public async Task An_untouched_aggregate_is_not_written()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        InvoiceId id = await CreateInvoiceAsync(ct);

        await using AppDbContext context = UnauditedContext();
        Invoice invoice = await context.Invoices.SingleAsync(loaded => loaded.Id == id, ct);
        uint versionBefore = invoice.Version;

        (await context.SaveChangesAsync(ct)).ShouldBe(0);
        invoice.Version.ShouldBe(versionBefore);
    }

    private async Task<InvoiceId> CreateInvoiceAsync(CancellationToken ct)
        => (await Services.GetRequiredService<ICommandHandler<CreateInvoiceCommand, InvoiceId>>()
            .Handle(new CreateInvoiceCommand("ACME", "a@b.co", "USD"), ct)).Value;

    /// <summary>The real context over the fixture database, but with none of the host's interceptors registered.</summary>
    private AppDbContext UnauditedContext()
    {
        string connectionString = Services.GetRequiredService<AppDbContext>().Database.GetConnectionString()!;
        DbContextOptions<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString).Options;
        return ActivatorUtilities.CreateInstance<AppDbContext>(Services, options);
    }
}
