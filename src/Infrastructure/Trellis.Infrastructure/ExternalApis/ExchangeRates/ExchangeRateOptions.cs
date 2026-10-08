using System.ComponentModel.DataAnnotations;

namespace Trellis.Infrastructure.ExternalApis.ExchangeRates;

/// <summary>
/// Settings for the exchange-rate provider, bound from the "ExternalApis:ExchangeRates" section and
/// validated at startup (see <c>AddOutboundIntegrations</c>). Every field carries a DataAnnotation so
/// a typo in configuration fails the host at boot with a precise message, instead of surfacing as a
/// <see cref="NullReferenceException"/> or a mystifying timeout on the first real call.
/// </summary>
public sealed class ExchangeRateOptions
{
    public const string SectionName = "ExternalApis:ExchangeRates";

    /// <summary>
    /// Provider root; a trailing slash is added if missing, so relative paths resolve beneath it. The
    /// default is Frankfurter (ECB reference rates, no access key); its <c>latest?base=&amp;symbols=</c>
    /// answer is what <see cref="ExchangeRateClient"/> reads.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [Url]
    public string BaseUrl { get; init; } = "https://api.frankfurter.dev/v1/";

    /// <summary>
    /// Per-attempt timeout. This drives the resilience handler's attempt timeout rather than
    /// <see cref="HttpClient.Timeout"/>, which would cap the whole pipeline including retries.
    /// </summary>
    [Range(1, 300)]
    public int TimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Retries granted to a transient failure. Small on purpose: retrying a slow third party mostly
    /// multiplies load. The floor is 1, not 0 — the standard resilience handler validates
    /// <c>MaxRetryAttempts >= 1</c> and throws at first use otherwise. "No retries" is expressed by
    /// removing the handler, not by setting this to zero.
    /// </summary>
    [Range(1, 5)]
    public int MaxRetries { get; init; } = 2;
}
