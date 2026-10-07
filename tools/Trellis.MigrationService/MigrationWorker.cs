using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
#if (UseMultitenancy)
using Trellis.Application.Abstractions;
#endif
using Trellis.Application.Authorization;
using Trellis.Domain.Common;
using Trellis.Domain.Invoices;
using Trellis.Persistence;
using Trellis.Persistence.Identity;

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

            await SeedRolesAndPermissionsAsync(services.GetRequiredService<RoleManager<IdentityRole<Guid>>>());
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
            logger.LogError(exception, "Database migration failed");
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

    private static async Task SeedRolesAndPermissionsAsync(RoleManager<IdentityRole<Guid>> roleManager)
    {
        foreach ((string roleName, IReadOnlyList<string> permissions) in Roles.Permissions)
        {
            IdentityRole<Guid>? role = await roleManager.FindByNameAsync(roleName);
            if (role is null)
            {
                role = new IdentityRole<Guid>(roleName) { Id = Guid.CreateVersion7() };
                await roleManager.CreateAsync(role);
            }

            var existing = (await roleManager.GetClaimsAsync(role))
                .Where(c => c.Type == AuthClaims.Permission)
                .Select(c => c.Value)
                .ToHashSet(StringComparer.Ordinal);

            foreach (string permission in permissions.Where(p => !existing.Contains(p)))
            {
                await roleManager.AddClaimAsync(role, new Claim(AuthClaims.Permission, permission));
            }
        }
    }

    private async Task SeedAdminUserAsync(UserManager<ApplicationUser> userManager)
    {
        if (await userManager.FindByEmailAsync(seedOptions.AdminEmail) is not null)
        {
            return;
        }

        if (!environment.IsDevelopment() && seedOptions.UsesDefaultAdminPassword)
        {
            throw new InvalidOperationException(
                "Refusing to seed the well-known default admin password outside Development. "
                + $"Set {SeedOptions.SectionName}__AdminPassword (and consider {SeedOptions.SectionName}__AdminEmail) for this environment.");
        }

        var admin = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            UserName = seedOptions.AdminEmail,
            Email = seedOptions.AdminEmail,
            EmailConfirmed = true,
#if (UseMultitenancy)
            TenantId = seedOptions.TenantId,
#endif
        };

        await userManager.CreateAsync(admin, seedOptions.AdminPassword);
        await userManager.AddToRoleAsync(admin, Roles.Administrator);
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
        overdue.AddLine("Consulting", Money.Create(1200m, usd).Value);
        overdue.Issue(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero), now: new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero));

        Invoice paid = Invoice.Create("Prompt Payers Ltd", email, eur).Value;
        paid.AddLine("Design sprint", Money.Create(4800m, eur).Value);
        paid.Issue(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero), now: new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        paid.MarkPaid(new DateTimeOffset(2026, 6, 20, 9, 30, 0, TimeSpan.Zero));
        paid.ClearDomainEvents();   // seeding is not a business payment — no receipt email

        Invoice draft = Invoice.Create("Draft & Co", email, usd).Value;
        draft.AddLine("Discovery workshop", Money.Create(950m, usd).Value);

        context.Invoices.AddRange(overdue, paid, draft);
        await context.SaveChangesAsync(cancellationToken);
    }
}
