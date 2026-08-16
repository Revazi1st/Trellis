using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Trellis.UI.Common.Auth;
using Trellis.UI.Common.Services;

namespace Trellis.UI.Common;

public static class DependencyInjection
{
    /// <summary>Named handler chain: pooled sockets + whatever ServiceDefaults layers on (resilience).</summary>
    private const string ApiClientName = "trellis-api";

    /// <summary>
    /// Registers the shared UI service layer (typed API clients, bearer handler, auth state).
    /// The host supplies <paramref name="configureApiClient"/> (base address — e.g. service discovery)
    /// and must register its own <see cref="ITokenStore"/> for the platform.
    /// </summary>
    // Deliberately not named after the project: the template substitutes the project name into
    // identifiers, and a name containing a dot (e.g. "Acme.Shop") would emit invalid C#.
    public static IServiceCollection AddSharedUi(
        this IServiceCollection services,
        Action<IServiceProvider, HttpClient> configureApiClient)
    {
        services.AddHttpClient(ApiClientName);

        // Deliberately NOT AddHttpClient<T>(...).AddHttpMessageHandler<BearerTokenHandler>():
        // IHttpClientFactory builds and caches handler chains in its own DI scope, so a handler
        // that depends on the *scoped* token state would get a different instance than the Blazor
        // circuit's — the token would never be attached and every call would 401. Building the
        // chain here means BearerTokenHandler sees the caller's (circuit's) token source.
        // The auth client stays bearer-free: its endpoints are anonymous, and the bearer chain's
        // token source needs IAuthApiClient to refresh — attaching it here would be a DI cycle.
        services.AddScoped<IInvoiceApiClient>(sp => new InvoiceApiClient(CreateApiClient(sp, configureApiClient, withBearer: true)));
        services.AddScoped<IAuthApiClient>(sp => new AuthApiClient(CreateApiClient(sp, configureApiClient, withBearer: false)));

        services.AddAuthorizationCore();
        services.AddScoped<TokenAuthenticationStateProvider>();
        services.AddScoped<AuthenticationStateProvider>(static sp => sp.GetRequiredService<TokenAuthenticationStateProvider>());
        services.AddScoped<IAccessTokenSource>(static sp => sp.GetRequiredService<TokenAuthenticationStateProvider>());
        services.AddCascadingAuthenticationState();

        return services;
    }

    private static HttpClient CreateApiClient(
        IServiceProvider sp,
        Action<IServiceProvider, HttpClient> configure,
        bool withBearer)
    {
        // The primary chain stays factory-owned (socket pooling, resilience) — hence disposeHandler: false.
        HttpMessageHandler primary = sp.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(ApiClientName);
        HttpMessageHandler top = withBearer
            ? new BearerTokenHandler(sp.GetRequiredService<IAccessTokenSource>()) { InnerHandler = primary }
            : primary;

        HttpClient client = new(top, disposeHandler: false);
        configure(sp, client);
        return client;
    }
}
