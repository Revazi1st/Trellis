using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Trellis.Application.Abstractions;
using Trellis.Application.Options;
using Trellis.Infrastructure.Authentication;
using Trellis.Infrastructure.Email;
using Trellis.Infrastructure.ExternalApis.ExchangeRates;

namespace Trellis.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Non-persistence adapters: clock, email, JWT token minting, outbound HTTP integrations.</summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<IEmailSender, LoggingEmailSender>();

        // Every options type in the solution is bound this way — annotations declared next to the
        // property they constrain, instead of a Get<T>() with a `?? new T()` fallback that turns a
        // missing section into silently-defaulted behaviour (for a signing key: an empty one).
        // Note there is no ValidateOnStart here: see RequireValidTokenOptions below for why.
        services.AddOptions<AuthTokenOptions>()
            .Bind(configuration.GetSection(AuthTokenOptions.SectionName))
            .ValidateDataAnnotations()
            // [MinLength] alone would say "the field SigningKey must be a string with a minimum
            // length of 32" — true, but it doesn't tell you what to do about it.
            .Validate(
                static options => options.SigningKey.Length >= AuthTokenOptions.MinimumSigningKeyLength,
                "Authentication:SigningKey is missing or shorter than 32 characters (256 bits for HMAC-SHA256). "
                + "Set it via user secrets in development "
                + "(dotnet user-secrets set \"Authentication:SigningKey\" \"<random 32+ chars>\") "
                + "or via the environment/secret store in production.");

        // Application handlers take the plain AuthTokenOptions, not IOptions<T> — the inner rings
        // stay free of the configuration machinery. Projecting .Value keeps that true while the
        // validation above still runs: resolving it here cannot dodge ValidateOnStart, which is
        // wired as a startup validator rather than a resolution-time one.
        services.AddSingleton(static provider => provider.GetRequiredService<IOptions<AuthTokenOptions>>().Value);

        // Same shape as AuthTokenOptions: bound, annotated, projected as the plain type the register
        // handler takes, and promoted to boot-time validation by RequireValidTokenOptions. A section
        // that is absent means Closed (the type's default), never an accidental Open.
        services.AddOptions<RegistrationOptions>()
            .Bind(configuration.GetSection(RegistrationOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                static options => Enum.IsDefined(options.Mode),
                "Registration:Mode must be Closed or Open. Leave it unset (Closed) unless anyone on the "
                + "network should be able to create an account.");
        services.AddSingleton(static provider => provider.GetRequiredService<IOptions<RegistrationOptions>>().Value);

        services.AddScoped<ITokenProvider, JwtTokenProvider>();

        return services.AddExchangeRateProvider(configuration);
    }

    /// <summary>
    /// Promotes the <see cref="AuthTokenOptions"/> and <see cref="RegistrationOptions"/> rules from
    /// "checked when first read" to "checked at boot". Call it from hosts that actually mint or
    /// validate tokens — the hosts that serve the auth endpoints.
    /// <para>
    /// It is a separate call rather than part of <see cref="AddInfrastructure"/> because
    /// ValidateOnStart is host policy, not adapter wiring. The migration service shares this
    /// Infrastructure registration but has no business refusing to run EF migrations because nobody
    /// gave it a JWT secret it will never use — while the API must refuse exactly that.
    /// </para>
    /// </summary>
    public static IServiceCollection RequireValidTokenOptions(this IServiceCollection services)
    {
        services.AddOptions<AuthTokenOptions>().ValidateOnStart();
        services.AddOptions<RegistrationOptions>().ValidateOnStart();
        return services;
    }

    /// <summary>
    /// The template's reference outbound HTTP integration (ADR-0020). Read the comments before
    /// copying this for a new third party — the defaults that look harmless are the ones that bite.
    /// </summary>
    private static IServiceCollection AddExchangeRateProvider(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ExchangeRateOptions>()
            .Bind(configuration.GetSection(ExchangeRateOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Resolved once here, up front, because the resilience handler below is configured at
        // registration time and needs the attempt timeout as a value, not a lazy IOptions.
        ExchangeRateOptions options =
            configuration.GetSection(ExchangeRateOptions.SectionName).Get<ExchangeRateOptions>() ?? new ExchangeRateOptions();
        TimeSpan attemptTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        services.AddHttpClient<IExchangeRateProvider, ExchangeRateClient>(static (provider, client) =>
        {
            ExchangeRateOptions current = provider.GetRequiredService<IOptions<ExchangeRateOptions>>().Value;
            string baseUrl = current.BaseUrl.EndsWith('/') ? current.BaseUrl : current.BaseUrl + "/";
            client.BaseAddress = new Uri(baseUrl);

            // The resilience pipeline owns every timeout. Leaving HttpClient.Timeout at its 100s
            // default would cap the WHOLE pipeline — retries included — and race the per-attempt
            // timeout, producing cancellations that look nothing like what you configured.
            client.Timeout = Timeout.InfiniteTimeSpan;
        })
        .AddStandardResilienceHandler(resilience =>
        {
            // ServiceDefaults applies AddStandardResilienceHandler() to ALL clients, and its default
            // per-attempt timeout is 10 seconds. That is fine for a fast JSON API and silently fatal
            // for a slow one: every attempt is cancelled at 10s no matter what the client's own
            // timeout says. Any third party that can legitimately take longer needs its attempt
            // timeout raised HERE, per client — this block is that override.
            resilience.AttemptTimeout.Timeout = attemptTimeout;

            // The handler validates these against each other: the total must leave room for the
            // retries, and the breaker's sampling window must be at least twice the attempt timeout.
            resilience.TotalRequestTimeout.Timeout = attemptTimeout * (options.MaxRetries + 2);
            resilience.CircuitBreaker.SamplingDuration = attemptTimeout * 2;

            resilience.Retry.MaxRetryAttempts = options.MaxRetries;
        });

        // Singleton: the check caches its probe result, and a per-request instance would throw that
        // cache away every time (see ExchangeRateHealthCheck).
        services.AddSingleton<ExchangeRateHealthCheck>();
        services.AddHealthChecks()
            .AddCheck<ExchangeRateHealthCheck>(
                "exchange-rates",
                failureStatus: HealthStatus.Degraded,
                tags: ["external"]);

        return services;
    }
}
