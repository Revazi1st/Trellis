using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using XTemplate.Application.Abstractions;
using XTemplate.Domain.Invoices;
using XTemplate.Persistence.Identity;
using XTemplate.Persistence.Interceptors;
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
        services.AddScoped<DomainEventDispatchInterceptor>();
        services.AddScoped<SoftDeleteInterceptor>();
        services.AddScoped<AuditingInterceptor>();

        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
        {
            configureProvider(options);

            // Order matters: events first (handlers may change entities), then soft-delete flips,
            // then auditing stamps everything — including what the first two produced.
            options.AddInterceptors(
                serviceProvider.GetRequiredService<DomainEventDispatchInterceptor>(),
                serviceProvider.GetRequiredService<SoftDeleteInterceptor>(),
                serviceProvider.GetRequiredService<AuditingInterceptor>());
        });

        services.AddScoped<IUnitOfWork>(static sp => sp.GetRequiredService<AppDbContext>());
        services.AddScoped<IInvoiceRepository, InvoiceRepository>();

        services.AddIdentityCore<ApplicationUser>(static options =>
            {
                options.User.RequireUniqueEmail = true;
                options.Password.RequiredLength = 8;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>();

        services.AddScoped<IIdentityService, IdentityService>();
        services.AddScoped<IRefreshTokenStore, RefreshTokenStore>();

        return services;
    }
}
