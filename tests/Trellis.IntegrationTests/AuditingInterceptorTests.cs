using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Domain.Invoices;
using Trellis.Persistence;
using Xunit;

namespace Trellis.IntegrationTests;

public sealed class AuditingInterceptorTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    [Fact]
    public async Task Creation_and_modification_are_stamped_with_clock_and_user_id()
    {
        string author = CurrentUser.UserId.ShouldNotBeNull().ToString();
        InvoiceId id = await Services.CreateInvoiceAsync();

        using (var scope = CreateScope())
        {
            Invoice? invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Invoices.FindAsync([id], TestContext.Current.CancellationToken);

            invoice!.CreatedAtUtc.ShouldBe(Now);
            invoice.CreatedBy.ShouldBe(author);
            invoice.ModifiedAtUtc.ShouldBeNull();
        }

        // Someone else edits it two hours later: the modification carries their id, never their e-mail,
        // and the creation keeps its author.
        Time.Advance(TimeSpan.FromHours(2));
        Guid colleague = Guid.CreateVersion7();
        CurrentUser.UserId = colleague;
        CurrentUser.UserName = "a-colleague@example.com";
        await Services.AddInvoiceLineAsync(id);

        using (var scope = CreateScope())
        {
            Invoice? invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
                .Invoices.FindAsync([id], TestContext.Current.CancellationToken);

            invoice!.ModifiedAtUtc.ShouldBe(Now.AddHours(2));
            invoice.ModifiedBy.ShouldBe(colleague.ToString());
            invoice.CreatedAtUtc.ShouldBe(Now);
            invoice.CreatedBy.ShouldBe(author);
        }
    }

    [Fact]
    public async Task A_caller_without_a_user_id_is_stamped_with_its_name()
    {
        // The migration job's SystemCurrentUser has no id; its rows say "system" rather than nothing.
        CurrentUser.UserId = null;
        CurrentUser.UserName = "system";

        InvoiceId id = await Services.CreateInvoiceAsync();

        using var scope = CreateScope();
        Invoice? invoice = await scope.ServiceProvider.GetRequiredService<AppDbContext>()
            .Invoices.FindAsync([id], TestContext.Current.CancellationToken);
        invoice!.CreatedBy.ShouldBe("system");
    }
}
