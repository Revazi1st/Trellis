using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using XTemplate.Domain.Common;
using XTemplate.Domain.Invoices;
using XTemplate.Persistence;

namespace XTemplate.MigrationService;

/// <summary>Applies EF migrations and seeds sample data, then stops the host — the API waits for successful completion (SSW pattern).</summary>
internal sealed class MigrationWorker(
    IServiceProvider serviceProvider,
    IHostApplicationLifetime hostApplicationLifetime,
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
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await ApplyMigrationsAsync(context, stoppingToken);
            await SeedSampleDataAsync(context, stoppingToken);

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
