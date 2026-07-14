using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using XTemplate.UI.Common.Auth;
using XTemplate.UI.Common.Services;

namespace XTemplate.UI.Common;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the shared UI service layer (typed API clients, bearer handler, auth state).
    /// The host supplies <paramref name="configureApiClient"/> (base address — e.g. service discovery)
    /// and must register its own <see cref="ITokenStore"/> for the platform.
    /// </summary>
    public static IServiceCollection AddXTemplateUi(
        this IServiceCollection services,
        Action<IServiceProvider, HttpClient> configureApiClient)
    {
        services.AddTransient<BearerTokenHandler>();

        services.AddHttpClient<IInvoiceApiClient, InvoiceApiClient>(configureApiClient)
            .AddHttpMessageHandler<BearerTokenHandler>();
        services.AddHttpClient<IAuthApiClient, AuthApiClient>(configureApiClient)
            .AddHttpMessageHandler<BearerTokenHandler>();

        services.AddAuthorizationCore();
        services.AddScoped<TokenAuthenticationStateProvider>();
        services.AddScoped<AuthenticationStateProvider>(static sp => sp.GetRequiredService<TokenAuthenticationStateProvider>());
        services.AddCascadingAuthenticationState();

        return services;
    }
}
