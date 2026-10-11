using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Domain.Invoices;
using Trellis.Persistence;
using Xunit;

namespace Trellis.IntegrationTests;

public sealed class AuditingInterceptorTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    [Fact]
    public async Task Creation_and_modification_are_stamped_with_clock_and_user()
    {
        InvoiceId id = await Services.CreateInvoiceAsync();

        using (var scope = CreateScope())
        {
            Invoice? invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Invoices.FindAsync([id], TestContext.Current.CancellationToken);

            invoice!.CreatedAtUtc.ShouldBe(Now);
            invoice.CreatedBy.ShouldBe(TestCurrentUser.DefaultUserName);
            invoice.ModifiedAtUtc.ShouldBeNull();
        }

        // Someone else edits it two hours later: the modification carries them, the creation keeps its author.
        Time.Advance(TimeSpan.FromHours(2));
        CurrentUser.UserName = "a-colleague";
        await Services.AddInvoiceLineAsync(id);

        using (var scope = CreateScope())
        {
            Invoice? invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Invoices.FindAsync([id], TestContext.Current.CancellationToken);

            invoice!.ModifiedAtUtc.ShouldBe(Now.AddHours(2));
            invoice.ModifiedBy.ShouldBe("a-colleague");
            invoice.CreatedAtUtc.ShouldBe(Now);
            invoice.CreatedBy.ShouldBe(TestCurrentUser.DefaultUserName);
        }
    }
}
