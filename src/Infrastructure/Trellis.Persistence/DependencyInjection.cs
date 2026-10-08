using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Trellis.Application.Abstractions;
using Trellis.Domain.Invoices;
using Trellis.Persistence.Idempotency;
using Trellis.Persistence.Identity;
using Trellis.Persistence.Interceptors;
using Trellis.Persistence.Outbox;
using Trellis.Persistence.Repositories;
using Trellis.SharedKernel.Domain;

namespace Trellis.Persistence;

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

    /// <summary>
    /// Registers the background outbox processor with its knobs from the "Outbox" section. Call from
    /// long-running hosts only (e.g. the API), not the one-shot MigrationService.
    /// <para>
    /// Same pattern as <see cref="AddDataRetention"/>: the section is bound here and the cross-field rule
    /// checked here; the per-property limits are DataAnnotations on <see cref="OutboxOptions"/> that the
    /// host promotes to a boot-time check with
    /// <c>services.AddOptions&lt;OutboxOptions&gt;().ValidateDataAnnotations().ValidateOnStart()</c>
    /// (the API does, next to the retention line). <c>OptionsConventionTests</c> fails if it stops.
    /// </para>
    /// </summary>
    public static IServiceCollection AddOutboxProcessing(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OutboxOptions>()
            .Bind(configuration.GetSection(OutboxOptions.SectionName))
            // The doubling is capped at MaxRetryDelay; a cap below the first delay would make every later
            // retry wait less than the first one, which nobody means.
            .Validate(static o => o.MaxRetryDelay >= o.BaseRetryDelay, "Outbox:MaxRetryDelay must be >= Outbox:BaseRetryDelay.");

        // The processor takes the plain POCO (tests construct one directly); resolving .Value runs every
        // registered validator, and ValidateOnStart (host policy) runs them at boot.
        services.AddSingleton(static provider =>
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<OutboxOptions>>().Value);
        services.AddSingleton<OutboxMetrics>();
        services.AddHostedService<OutboxProcessor>();
        return services;
    }

    /// <summary>
    /// Registers the retention janitor that prunes processed outbox rows, stale idempotency keys and
    /// dead refresh tokens (windows from the "Retention" section). Long-running hosts only.
    /// <para>
    /// The windows are bound here and validated by the host that calls this: <see cref="Maintenance.RetentionOptions"/>
    /// carries the DataAnnotations, and the host promotes them to a boot-time check with
    /// <c>services.AddOptions&lt;RetentionOptions&gt;().ValidateDataAnnotations().ValidateOnStart()</c>
    /// (the API does, next to <c>RequireValidTokenOptions</c>). Persistence does not reference the
    /// DataAnnotations options extension, so it cannot run them itself; <c>OptionsConventionTests</c>
    /// fails if the API host stops validating them.
    /// </para>
    /// </summary>
    public static IServiceCollection AddDataRetention(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<Maintenance.RetentionOptions>()
            .Bind(configuration.GetSection(Maintenance.RetentionOptions.SectionName));

        // The janitor takes the plain POCO, like the handlers take AuthTokenOptions; resolving .Value
        // runs every registered validator, and ValidateOnStart (host policy) runs them at boot.
        services.AddSingleton(static provider =>
            provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<Maintenance.RetentionOptions>>().Value);
        services.AddHostedService<Maintenance.DataRetentionJanitor>();
        return services;
    }
}
