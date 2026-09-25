using Trellis.SharedKernel.Results;

namespace Trellis.Application.Abstractions;

/// <summary>
/// Outbound port for foreign-exchange rates — the template's reference example of depending on a
/// third-party HTTP API. The Infrastructure adapter owns the transport concerns (typed client,
/// per-client resilience, timeouts); everything inward of this interface only ever sees a
/// <see cref="Result{T}"/>.
/// </summary>
public interface IExchangeRateProvider
{
    /// <summary>
    /// The rate that converts one unit of <paramref name="baseCurrency"/> into
    /// <paramref name="quoteCurrency"/> (both ISO-4217, e.g. "EUR"). A third party being slow,
    /// unreachable or nonsensical is an expected outcome, so it comes back as a failed Result
    /// rather than an exception (ADR-0004).
    /// </summary>
    Task<Result<decimal>> GetRateAsync(string baseCurrency, string quoteCurrency, CancellationToken cancellationToken);
}
