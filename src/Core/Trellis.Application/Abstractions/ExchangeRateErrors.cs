using Trellis.SharedKernel.Results;

namespace Trellis.Application.Abstractions;

/// <summary>Failures an <see cref="IExchangeRateProvider"/> can report. Transport detail stays in Infrastructure; callers see these.</summary>
public static class ExchangeRateErrors
{
    /// <summary>
    /// The provider could not be reached, or answered too slowly / with an error status. Deliberately
    /// a <see cref="ErrorType.Failure"/>: a third party being down is a 500-class problem for us, not
    /// something the caller can fix by changing their request.
    /// </summary>
    public static readonly Error Unavailable =
        Error.Failure("ExchangeRate.Unavailable", "The exchange-rate provider is currently unavailable.");

    /// <summary>The provider answered, but not with a shape we understand — treated as an outage, not a caller error.</summary>
    public static readonly Error MalformedResponse =
        Error.Failure("ExchangeRate.MalformedResponse", "The exchange-rate provider returned an unreadable response.");

    public static Error UnknownCurrency(string code) =>
        Error.Validation("ExchangeRate.UnknownCurrency", $"The exchange-rate provider does not publish a rate for '{code}'.");
}
