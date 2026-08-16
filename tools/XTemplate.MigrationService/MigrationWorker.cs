using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using XTemplate.Application.Authorization;
using XTemplate.Domain.Common;
using XTemplate.Domain.Invoices;
using XTemplate.Persistence;
using XTemplate.Persistence.Identity;

namespace XTemplate.MigrationService;

/// <summary>Applies EF migrations, seeds roles/permissions/admin and sample data, then stops the host — the API waits for successful completion (SSW pattern).</summary>
internal sealed class MigrationWorker(
    IServiceProvider serviceProvider,
    IHostApplicationLifetime hostApplicationLifetime,
    IHostEnvironment environment,
    SeedOptions seedOptions,
    ILogger<MigrationWorker> logger) : BackgroundService
{
    public const string ActivitySourceName = "XTemplate.MigrationService";

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
        };

        await userManager.CreateAsync(admin, seedOptions.AdminPassword);
        await userManager.AddToRoleAsync(admin, Roles.Administrator);
    }

    private static async Task SeedSampleDataAsync(AppDbContext context, CancellationToken cancellationToken)
    {
        if (await context.Invoices.AnyAsync(cancellationToken))
        {
            return;
        }

        EmailAddress email = EmailAddress.Create("billing@sample.dev").Value;

        Invoice overdue = Invoice.Create("Overdue Industries", email, "USD").Value;
        overdue.AddLine("Consulting", Money.Create(1200m, "USD").Value);
        overdue.Issue(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));

        Invoice paid = Invoice.Create("Prompt Payers Ltd", email, "EUR").Value;
        paid.AddLine("Design sprint", Money.Create(4800m, "EUR").Value);
        paid.Issue(new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero));
        paid.MarkPaid(new DateTimeOffset(2026, 6, 20, 9, 30, 0, TimeSpan.Zero));
        paid.ClearDomainEvents();   // seeding is not a business payment — no receipt email

        Invoice draft = Invoice.Create("Draft & Co", email, "USD").Value;
        draft.AddLine("Discovery workshop", Money.Create(950m, "USD").Value);

        context.Invoices.AddRange(overdue, paid, draft);
        await context.SaveChangesAsync(cancellationToken);
    }
}
