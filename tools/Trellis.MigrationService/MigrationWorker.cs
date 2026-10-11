using System.Diagnostics;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
#if (UseMultitenancy)
using Trellis.Application.Abstractions;
#endif
using Trellis.Domain.Common;
using Trellis.Domain.Invoices;
using Trellis.Persistence;
using Trellis.Persistence.Identity;
using Trellis.SharedKernel.Results;

namespace Trellis.MigrationService;

/// <summary>Applies EF migrations, seeds roles/permissions/admin and sample data, then stops the host — the API waits for successful completion (SSW pattern).</summary>
internal sealed class MigrationWorker(
    IServiceProvider serviceProvider,
    IHostApplicationLifetime hostApplicationLifetime,
    IHostEnvironment environment,
    SeedOptions seedOptions,
    ILogger<MigrationWorker> logger) : BackgroundService
{
    public const string ActivitySourceName = "Trellis.MigrationService";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using Activity? activity = ActivitySource.StartActivity("Migrate and seed database");

        try
        {
            using IServiceScope scope = serviceProvider.CreateScope();
            IServiceProvider services = scope.ServiceProvider;

            AppDbContext context = services.GetRequiredService<AppDbContext>();
            await ApplyMigrationsAsync(context, stoppingToken);

            await IdentitySeed.EnsureRolesAndPermissionsAsync(services.GetRequiredService<RoleManager<IdentityRole<Guid>>>());
            await SeedAdminUserAsync(services.GetRequiredService<UserManager<ApplicationUser>>());

            // Demo invoices are a local-dev convenience only.
            if (environment.IsDevelopment())
            {
                await SeedSampleDataAsync(context, stoppingToken);
            }

            logger.LogInformation("Database migration and seeding completed");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Database migration or seeding failed");
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            Environment.ExitCode = 1;
        }

        hostApplicationLifetime.StopApplication();
    }

    private static async Task ApplyMigrationsAsync(AppDbContext context, CancellationToken cancellationToken)
    {
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(() => context.Database.MigrateAsync(cancellationToken));
    }

    private async Task SeedAdminUserAsync(UserManager<ApplicationUser> userManager)
    {
        // The default-password guard applies only when the account is about to be created: an existing
        // admin's password is never touched, so a rerun with the default left in place is harmless.
        if (!environment.IsDevelopment()
            && seedOptions.UsesDefaultAdminPassword
            && await userManager.FindByEmailAsync(seedOptions.AdminEmail) is null)
        {
            throw new InvalidOperationException(
                "Refusing to seed the well-known default admin password outside Development. "
                + $"Set {SeedOptions.SectionName}__AdminPassword (and consider {SeedOptions.SectionName}__AdminEmail) for this environment.");
        }

        // Throws, naming every Identity error, when Identity rejects the account (a password that fails
        // its rules, a duplicate user name) — ExecuteAsync turns that into exit code 1, never "completed".
        await IdentitySeed.EnsureAdministratorAsync(
            userManager,
            seedOptions.AdminEmail,
            seedOptions.AdminPassword
#if (UseMultitenancy)
            , seedOptions.TenantId
#endif
            );
    }

#if (!UseMultitenancy)
    private static async Task SeedSampleDataAsync(AppDbContext context, CancellationToken cancellationToken)
    {
        if (await context.Invoices.AnyAsync(cancellationToken))
        {
            return;
        }
#else
    private async Task SeedSampleDataAsync(AppDbContext context, CancellationToken cancellationToken)
    {
        // The worker has no request, so SystemTenantContext yields no tenant; the demo rows must
        // belong to the seeded admin's tenant or the admin would never see them. AmbientTenant is
        // what the "Tenant" filter (for the AnyAsync probe below), the TenantInterceptor and the
        // outbox row for InvoiceIssued all read.
        AmbientTenant.CurrentTenantId = seedOptions.TenantId;
        try
        {
            await SeedSampleDataCoreAsync(context, cancellationToken);
        }
        finally
        {
            AmbientTenant.CurrentTenantId = null;
        }
    }

    private static async Task SeedSampleDataCoreAsync(AppDbContext context, CancellationToken cancellationToken)
    {
        if (await context.Invoices.AnyAsync(cancellationToken))
        {
            return;
        }
#endif

        EmailAddress email = EmailAddress.Create("billing@sample.dev").Value;
        Currency usd = Currency.Create("USD").Value;
        Currency eur = Currency.Create("EUR").Value;

        // Demo history: each invoice is issued "as of" a date before its due date, since Issue rejects a
        // due date that is already past at the moment of issuing.
        Invoice overdue = Invoice.Create("Overdue Industries", email, usd).Value;
        Seeded(overdue.AddLine("Consulting", Money.Create(1200m, usd).Value), "adding the overdue invoice's line");
        Seeded(
            overdue.Issue(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), now: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero)),
            "issuing the overdue invoice");

        Invoice paid = Invoice.Create("Prompt Payers Ltd", email, eur).Value;
        Seeded(paid.AddLine("Design sprint", Money.Create(4800m, eur).Value), "adding the paid invoice's line");
        Seeded(
            paid.Issue(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero), now: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)),
            "issuing the paid invoice");
        Seeded(paid.MarkPaid(new DateTimeOffset(2026, 6, 20, 9, 30, 0, TimeSpan.Zero)), "paying the paid invoice");
        paid.ClearDomainEvents();   // seeding is not a business payment — no receipt email

        Invoice draft = Invoice.Create("Draft & Co", email, usd).Value;
        Seeded(draft.AddLine("Discovery workshop", Money.Create(950m, usd).Value), "adding the draft invoice's line");

        context.Invoices.AddRange(overdue, paid, draft);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// A transition the domain refuses fails the run — ExecuteAsync turns the exception into exit code 1 —
    /// instead of saving a half-built sample (an "overdue" invoice still in Draft). Seeding is a one-shot
    /// deployment step with no caller a <see cref="Result"/> could inform, as in <c>IdentitySeed</c>.
    /// </summary>
    private static void Seeded(Result transition, string step)
    {
        if (transition.IsFailure)
        {
            throw new InvalidOperationException(
                $"Seeding the sample invoices failed while {step}: [{transition.Error.Code}] {transition.Error.Description}");
        }
    }
}
