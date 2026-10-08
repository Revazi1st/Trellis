using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using Trellis.Application.Abstractions;
using Trellis.Application.Options;
using Trellis.Infrastructure.Authentication;
using Trellis.Infrastructure.Email;
using Trellis.Infrastructure.ExternalApis.ExchangeRates;

namespace Trellis.Infrastructure;

public static class DependencyInjection
{
    /// <summary>First wait before a retry of an outbound call (the standard handler's own default, stated).</summary>
    private static readonly TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(2);

    /// <summary>Longest single wait between retries, whatever the exponent and jitter produce.</summary>
    private static readonly TimeSpan RetryMaxDelay = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Non-persistence adapters every host needs: clock, email, JWT token minting. Outbound HTTP
    /// integrations are not here — see <see cref="AddOutboundIntegrations"/>.
    /// </summary>
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddEmailSender(configuration);

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

        return services;
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
    /// What a host that calls out of the process needs, checked at boot: the outbound HTTP
    /// integrations (ADR-0020) — typed clients, their resilience pipelines and health checks — and the
    /// e-mail sender's configuration, with a startup notice when e-mail goes nowhere. Register it only
    /// in hosts that call out: the API (it also drains the outbox, so it is the host that sends e-mail).
    /// <para>
    /// A separate call from <see cref="AddInfrastructure"/> for the reason <see cref="RequireValidTokenOptions"/>
    /// is: the migration service shares that registration but never calls a third party, so nothing
    /// in an <c>ExternalApis</c> section may stop it from running EF migrations. Validating "only in
    /// the API" is not enough on its own here: the standard resilience handler validates its own
    /// options at boot, and those are computed from the integration's options, so merely registering
    /// a client makes its section a boot requirement of the host. The host that registers the client
    /// is therefore the host that opts in, and it validates the whole section at boot.
    /// </para>
    /// </summary>
    public static IServiceCollection AddOutboundIntegrations(this IServiceCollection services, IConfiguration configuration)
    {
        // The sender itself is registered by AddInfrastructure in every host — Application's event
        // handlers must be constructible anywhere — but only this host sends, so only it checks the
        // section at boot and says so when the configured provider sends nothing.
        services.AddOptions<EmailOptions>().ValidateOnStart();
        services.AddHostedService<EmailProviderStartupNotice>();

        return services.AddExchangeRateProvider(configuration);
    }

    /// <summary>
    /// The <see cref="IEmailSender"/> adapter named by <c>Email:Provider</c>, resolved per scope. The
    /// options are validated when the sender is first built; a host that sends promotes that to boot
    /// (<see cref="AddOutboundIntegrations"/>).
    /// </summary>
    private static void AddEmailSender(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(
                static options => Enum.IsDefined(options.Provider),
                "Email:Provider must be Logging (development stand-in: logs a masked line, sends nothing) or None "
                + "(discard). A real provider is a new adapter in Infrastructure/Email.");

        services.AddScoped<LoggingEmailSender>();
        services.AddScoped<DiscardingEmailSender>();
        services.AddScoped<IEmailSender>(static provider =>
            provider.GetRequiredService<IOptions<EmailOptions>>().Value.Provider switch
            {
                EmailProvider.Logging => provider.GetRequiredService<LoggingEmailSender>(),
                EmailProvider.None => provider.GetRequiredService<DiscardingEmailSender>(),
                // Unreachable: the validation above rejects any other value before this runs.
                var other => throw new InvalidOperationException($"Email:Provider '{other}' has no adapter."),
            });
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

        IHttpClientBuilder client = services.AddHttpClient<IExchangeRateProvider, ExchangeRateClient>(static (provider, client) =>
        {
            ExchangeRateOptions current = provider.GetRequiredService<IOptions<ExchangeRateOptions>>().Value;
            string baseUrl = current.BaseUrl.EndsWith('/') ? current.BaseUrl : current.BaseUrl + "/";
            client.BaseAddress = new Uri(baseUrl);

            // The resilience pipeline owns every timeout. Leaving HttpClient.Timeout at its 100s
            // default would cap the WHOLE pipeline — retries included — and race the per-attempt
            // timeout, producing cancellations that look nothing like what you configured.
            client.Timeout = Timeout.InfiniteTimeSpan;
        });

        // REPLACE the blanket pipeline, do not add to it. ServiceDefaults puts a standard resilience
        // handler on EVERY client (10 s per attempt, 30 s total, 3 retries). A second
        // AddStandardResilienceHandler on its own would nest inside that one: the outer 10 s still
        // cancels each attempt whatever this client configures, and the retries multiply (each outer
        // attempt runs this client's whole retry loop). So the inherited handler goes first.
        // EXTEXP0001: experimental API, kept on purpose. The non-experimental equivalent is
        // ConfigureAdditionalHttpMessageHandlers removing every ResilienceHandler from the list.
#pragma warning disable EXTEXP0001
        client.RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

        client.AddStandardResilienceHandler().Configure(ConfigureExchangeRateResilience);

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

    /// <summary>
    /// The exchange-rate client's own pipeline. It reads <see cref="ExchangeRateOptions"/> through the
    /// options system when the pipeline is first built — the same validated value the client sees —
    /// rather than from a snapshot bound at registration time.
    /// </summary>
    private static void ConfigureExchangeRateResilience(HttpStandardResilienceOptions resilience, IServiceProvider provider)
    {
        ExchangeRateOptions options = provider.GetRequiredService<IOptions<ExchangeRateOptions>>().Value;
        TimeSpan attemptTimeout = TimeSpan.FromSeconds(options.TimeoutSeconds);

        resilience.AttemptTimeout.Timeout = attemptTimeout;
        resilience.Retry.MaxRetryAttempts = options.MaxRetries;

        // Exponential back-off with jitter, every wait capped, so the worst case is a number.
        resilience.Retry.Delay = RetryBaseDelay;
        resilience.Retry.MaxDelay = RetryMaxDelay;

        // The total budget is the worst case of what the lines above grant: every attempt runs to its
        // timeout AND every wait is the longest one. Anything smaller lets the total timeout cut off a
        // retry the policy promised (a budget counted in attempts alone forgets the back-off).
        resilience.TotalRequestTimeout.Timeout =
            (attemptTimeout * (options.MaxRetries + 1)) + (RetryMaxDelay * options.MaxRetries);

        // The handler validates the breaker's sampling window to be at least twice the attempt timeout.
        resilience.CircuitBreaker.SamplingDuration = attemptTimeout * 2;
    }
}
