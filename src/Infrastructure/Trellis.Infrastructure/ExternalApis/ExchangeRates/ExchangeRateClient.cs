using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Trellis.Application.Abstractions;
using Trellis.SharedKernel.Results;

namespace Trellis.Infrastructure.ExternalApis.ExchangeRates;

/// <summary>
/// Typed <see cref="HttpClient"/> adapter for <see cref="IExchangeRateProvider"/>. The client itself
/// stays deliberately thin: base address, timeout and the retry/circuit-breaker pipeline are all
/// configured once at registration (see <c>AddInfrastructure</c>), so this file only maps one HTTP
/// call onto the port's Result contract.
/// </summary>
/// <remarks>
/// The three catch blocks are the whole point of the adapter: everything the transport can throw is
/// converted into a Result here, at the boundary, so no exception from a third party ever escapes
/// into Application code. <see cref="TaskCanceledException"/> is caught only when it is NOT the
/// caller's cancellation — a timeout surfaces as the same exception type as a real cancellation, and
/// swallowing the latter would break cooperative shutdown.
/// </remarks>
internal sealed class ExchangeRateClient(HttpClient httpClient, ILogger<ExchangeRateClient> logger)
    : IExchangeRateProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<Result<decimal>> GetRateAsync(
        string baseCurrency,
        string quoteCurrency,
        CancellationToken cancellationToken)
    {
        string requestUri =
            $"latest?base={Uri.EscapeDataString(baseCurrency)}&symbols={Uri.EscapeDataString(quoteCurrency)}";

        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(requestUri, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Exchange-rate provider answered {StatusCode} for {Base}->{Quote}.",
                    (int)response.StatusCode, baseCurrency, quoteCurrency);
                return Result.Failure<decimal>(ExchangeRateErrors.Unavailable);
            }

            ExchangeRateResponse? payload = await response.Content
                .ReadFromJsonAsync<ExchangeRateResponse>(SerializerOptions, cancellationToken);

            if (payload?.Rates is null)
            {
                return Result.Failure<decimal>(ExchangeRateErrors.MalformedResponse);
            }

            return payload.Rates.TryGetValue(quoteCurrency, out decimal rate)
                ? Result.Success(rate)
                : Result.Failure<decimal>(ExchangeRateErrors.UnknownCurrency(quoteCurrency));
        }
        catch (HttpRequestException exception)
        {
            // Transport-level failure that outlived the retry pipeline (DNS, connection reset, an
            // open circuit breaker). Already retried — there is nothing left to do but report it.
            logger.LogWarning(exception, "Exchange-rate provider unreachable for {Base}->{Quote}.", baseCurrency, quoteCurrency);
            return Result.Failure<decimal>(ExchangeRateErrors.Unavailable);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // A timeout, not the caller giving up: the resilience handler's attempt/total timeout
            // cancels the request with this same exception type.
            logger.LogWarning(exception, "Exchange-rate provider timed out for {Base}->{Quote}.", baseCurrency, quoteCurrency);
            return Result.Failure<decimal>(ExchangeRateErrors.Unavailable);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Exchange-rate provider returned unreadable JSON for {Base}->{Quote}.", baseCurrency, quoteCurrency);
            return Result.Failure<decimal>(ExchangeRateErrors.MalformedResponse);
        }
    }

    /// <summary>Wire shape of the provider's /latest response; kept private so the third party's schema never leaks inward.</summary>
    private sealed record ExchangeRateResponse
    {
        [JsonPropertyName("rates")]
        public Dictionary<string, decimal>? Rates { get; init; }
    }
}
