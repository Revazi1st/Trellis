using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XTemplate.Application.Abstractions;
using XTemplate.Domain.Invoices;
using XTemplate.Persistence.Idempotency;
using XTemplate.Persistence.Identity;
using XTemplate.Persistence.Interceptors;
using XTemplate.Persistence.Outbox;
using XTemplate.Persistence.Repositories;
using XTemplate.SharedKernel.Domain;

namespace XTemplate.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, string connectionString)
        => services.AddPersistence(options => options.UseNpgsql(
            connectionString,
            static npgsql => npgsql.EnableRetryOnFailure()));

    public static IServiceCollection AddPersistence(
        this IServiceCollection services,
        Action<DbContextOptionsBuilder> configureProvider)
    {
        services.AddScoped<OutboxWriteInterceptor>();
        services.AddScoped<SoftDeleteInterceptor>();
        services.AddScoped<AuditingInterceptor>();
#if (UseMultitenancy)
        services.AddScoped<TenantInterceptor>();
#endif

        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            configureProvider(options);

            // Order matters: events first (handlers may change entities), then soft-delete flips,
            // then auditing stamps everything — including what the first two produced.
            options.AddInterceptors(
                serviceProvider.GetRequiredService<OutboxWriteInterceptor>(),
                serviceProvider.GetRequiredService<SoftDeleteInterceptor>(),
                serviceProvider.GetRequiredService<AuditingInterceptor>()
#if (UseMultitenancy)
                , serviceProvider.GetRequiredService<TenantInterceptor>()
#endif
                );
        });

        services.AddScoped<IUnitOfWork>(static sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<IInvoiceRepository, InvoiceRepository>();

        services.AddIdentityCore<ApplicationUser>(static options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 8;
                // Per-account brute-force defense; the API's auth rate limit is only per source.
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>();

        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IRefreshTokenStore, RefreshTokenStore>();
        services.AddScoped<IIdempotencyStore, IdempotencyStore>();

        return services;
    }

    /// <summary>Registers the background outbox processor. Call from long-running hosts only (e.g. the API), not the one-shot MigrationService.</summary>
    public static IServiceCollection AddOutboxProcessing(this IServiceCollection services)
    {
        services.AddHostedService<OutboxProcessor>();
        return services;
    }
}
