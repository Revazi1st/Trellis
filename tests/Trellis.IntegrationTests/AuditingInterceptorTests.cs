using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Application.Features.Invoices.AddInvoiceLine;
using Trellis.Application.Features.Invoices.CreateInvoice;
using Trellis.Domain.Invoices;
using Trellis.Persistence;
using Trellis.SharedKernel.Messaging;
using Xunit;

namespace Trellis.IntegrationTests;

public sealed class AuditingInterceptorTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    [Fact]
    public async Task Creation_and_modification_are_stamped_with_clock_and_user()
    {
        var create = Services.GetRequiredService<ICommandHandler<CreateInvoiceCommand, InvoiceId>>();
        InvoiceId id = (await create.Handle(
            new CreateInvoiceCommand("ACME", "a@b.co", "USD"), TestContext.Current.CancellationToken)).Value;

        using (var scope = CreateScope())
        {
            Invoice? invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Invoices.FindAsync([id], TestContext.Current.CancellationToken);

            invoice!.CreatedAtUtc.ShouldBe(Now);
            invoice.CreatedBy.ShouldBe(CurrentUserName);
            invoice.ModifiedAtUtc.ShouldBeNull();
        }

        Time.Advance(TimeSpan.FromHours(2));
        var addLine = Services.GetRequiredService<ICommandHandler<AddInvoiceLineCommand>>();
        (await addLine.Handle(new AddInvoiceLineCommand(id, "Line", 10m), TestContext.Current.CancellationToken))
            .ShouldBeSuccess();

        using (var scope = CreateScope())
        {
            Invoice? invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Invoices.FindAsync([id], TestContext.Current.CancellationToken);

            invoice!.ModifiedAtUtc.ShouldBe(Now.AddHours(2));
            invoice.ModifiedBy.ShouldBe(CurrentUserName);
            invoice.CreatedAtUtc.ShouldBe(Now);
        }
    }
}
