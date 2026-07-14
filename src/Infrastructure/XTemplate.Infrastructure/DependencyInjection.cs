using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using XTemplate.Application.Abstractions;
using XTemplate.Application.Options;
using XTemplate.Infrastructure.Authentication;
using XTemplate.Infrastructure.Email;

namespace XTemplate.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Non-persistence adapters: clock, email, JWT token minting.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IEmailSender, LoggingEmailSender>();

        AuthTokenOptions authOptions = configuration.GetSection(AuthTokenOptions.SectionName).Get<AuthTokenOptions>()
            ?? new AuthTokenOptions();
        services.AddSingleton(authOptions);
        services.AddScoped<ITokenProvider, JwtTokenProvider>();

        return services;
    }
}
