using Trellis.SharedKernel.Results;

namespace Trellis.Application.Abstractions;

/// <summary>
/// Failures an <see cref="IExchangeRateProvider"/> can report. Transport detail stays in Infrastructure;
/// callers see these. The split that matters is whether waiting helps: <see cref="Unavailable"/> is a
/// provider that is down for now (503 at our API), <see cref="Rejected"/> and
/// <see cref="MalformedResponse"/> are ours to fix (500), <c>UnknownCurrency</c> is the caller's (400).
/// </summary>
public static class ExchangeRateErrors
{
    /// <summary>
    /// The provider could not be reached, answered too slowly, kept failing with a 5xx or a 429 after
    /// the retries, or our circuit breaker or concurrency limiter refused the call. An
    /// <see cref="ErrorType.Unavailable"/>: the same request may succeed later, and nothing the caller
    /// changes in it would help.
    /// </summary>
    public static readonly Error Unavailable =
        Error.Unavailable("ExchangeRate.Unavailable", "The exchange-rate provider is currently unavailable.");

    /// <summary>
    /// The provider answered, and refused: our credentials or plan (401/403), a route it does not serve,
    /// which means a wrong <c>BaseUrl</c> or a retired API version (404), any other refusal it reports, or
    /// a 200 that says <c>success:false</c>. A <see cref="ErrorType.Failure"/>: our configuration is wrong,
    /// so no retry and no change to the caller's request cures it.
    /// </summary>
    public static readonly Error Rejected =
        Error.Failure("ExchangeRate.Rejected", "The exchange-rate provider refused the request; the integration's configuration needs attention.");

    /// <summary>The provider answered, but not with a shape we understand — treated as our problem, not a caller error.</summary>
    public static readonly Error MalformedResponse =
        Error.Failure("ExchangeRate.MalformedResponse", "The exchange-rate provider returned an unreadable response.");

    /// <summary>The answer has no rate for <paramref name="code"/>: the feed does not publish that currency.</summary>
    public static Error UnknownCurrency(string code) =>
        Error.Validation("ExchangeRate.UnknownCurrency", $"The exchange-rate provider does not publish a rate for '{code}'.");
}
