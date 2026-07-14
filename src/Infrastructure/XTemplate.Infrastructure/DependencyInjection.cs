using Microsoft.Extensions.DependencyInjection;
using XTemplate.Application.Abstractions;
using XTemplate.Infrastructure.Email;

namespace XTemplate.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Non-persistence adapters: clock, email. JWT token provider joins at M6.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IEmailSender, LoggingEmailSender>();

        return services;
    }
}
