using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Polly;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Trellis.Application.Abstractions;
using Trellis.SharedKernel.Results;

namespace Trellis.Infrastructure.ExternalApis.ExchangeRates;

/// <summary>
/// Typed <see cref="HttpClient"/> adapter for <see cref="IExchangeRateProvider"/>. The client itself
/// stays deliberately thin: base address, timeout and the retry/circuit-breaker pipeline are all
/// configured once at registration (see <c>AddOutboundIntegrations</c>), so this file only maps one HTTP
/// call onto the port's Result contract.
/// </summary>
/// <remarks>
/// The catch blocks are the whole point of the adapter: everything the transport and the resilience
/// pipeline can throw is converted into a Result here, at the boundary, so no exception from a third
/// party ever escapes into Application code. What the pipeline throws is not what a bare
/// <see cref="HttpClient"/> throws: a timed-out attempt or budget surfaces as Polly's
/// <see cref="TimeoutRejectedException"/> (not <see cref="TaskCanceledException"/>), an open circuit as
/// <see cref="BrokenCircuitException"/>, a full concurrency limiter as another
/// <see cref="ExecutionRejectedException"/>. The caller's own cancellation is the one outcome that is
/// NOT converted: it propagates as <see cref="OperationCanceledException"/>, because swallowing it
/// would break cooperative shutdown and request abort.
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
        // Frankfurter v2: no date means the latest published rates.
        string requestUri =
            $"rates?base={Uri.EscapeDataString(baseCurrency)}&quotes={Uri.EscapeDataString(quoteCurrency)}";

        try
        {
            using HttpResponseMessage response = await httpClient.GetAsync(requestUri, cancellationToken);
            return await ReadRateAsync(response, baseCurrency, quoteCurrency, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            // Transport-level failure that outlived the retry pipeline (DNS, connection refused or
            // reset). Already retried — there is nothing left to do but report it.
            logger.LogWarning(exception, "Exchange-rate provider unreachable for {Base}->{Quote}.", baseCurrency, quoteCurrency);
            return Result.Failure<decimal>(ExchangeRateErrors.Unavailable);
        }
        catch (TimeoutRejectedException exception)
        {
            // The pipeline's attempt timeout fired on the last attempt, or its total budget ran out.
            logger.LogWarning(exception, "Exchange-rate provider timed out for {Base}->{Quote}.", baseCurrency, quoteCurrency);
            return Result.Failure<decimal>(ExchangeRateErrors.Unavailable);
        }
        catch (BrokenCircuitException exception)
        {
            // The circuit is open: recent calls failed often enough that the pipeline refuses to send
            // until the break duration has passed. No request left this process.
            logger.LogWarning(exception, "Exchange-rate circuit is open for {Base}->{Quote}; call not attempted.", baseCurrency, quoteCurrency);
            return Result.Failure<decimal>(ExchangeRateErrors.Unavailable);
        }
        catch (ExecutionRejectedException exception)
        {
            // Any other refusal by the pipeline: today the standard handler's concurrency limiter
            // (RateLimiterRejectedException) when more calls are in flight than it admits.
            logger.LogWarning(exception, "Exchange-rate pipeline rejected the call for {Base}->{Quote}.", baseCurrency, quoteCurrency);
            return Result.Failure<decimal>(ExchangeRateErrors.Unavailable);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Cancelled, but not by our caller: something below us gave up (an HttpClient.Timeout, if a
            // copy of this adapter ever reinstates one). The caller's own cancellation is excluded by
            // the filter and propagates untouched.
            logger.LogWarning(exception, "Exchange-rate call was cancelled below the adapter for {Base}->{Quote}.", baseCurrency, quoteCurrency);
            return Result.Failure<decimal>(ExchangeRateErrors.Unavailable);
        }
        catch (JsonException exception)
        {
            logger.LogWarning(exception, "Exchange-rate provider returned unreadable JSON for {Base}->{Quote}.", baseCurrency, quoteCurrency);
            return Result.Failure<decimal>(ExchangeRateErrors.MalformedResponse);
        }
    }

    private async Task<Result<decimal>> ReadRateAsync(
        HttpResponseMessage response,
        string baseCurrency,
        string quoteCurrency,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Exchange-rate provider answered {StatusCode} for {Base}->{Quote}.",
                (int)response.StatusCode, baseCurrency, quoteCurrency);

            // Credentials, plan or route refused: no wait cures our configuration. Anything else that
            // outlived the retries is the provider being down for now.
            return Result.Failure<decimal>(
                response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound
                    ? ExchangeRateErrors.Rejected
                    : ExchangeRateErrors.Unavailable);
        }

        // The root's kind decides how the body is read: Frankfurter answers an array, while a provider that
        // wraps its failures in a 200 answers an object. Anything unparsable throws JsonException, which the
        // caller maps to MalformedResponse.
        await using Stream content = await response.Content.ReadAsStreamAsync(cancellationToken);
        using JsonDocument body = await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken);
        JsonElement root = body.RootElement;

        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("success", out JsonElement success)
            && success.ValueKind == JsonValueKind.False)
        {
            // Some providers report their own failures (missing access key, exhausted quota) as a 200
            // whose body says success:false. That is the provider declining to answer, not a shape we
            // failed to read, and not an outage a retry outlasts: our key or plan needs attention.
            logger.LogError(
                "Exchange-rate provider answered success:false for {Base}->{Quote}; check the access key and plan.", baseCurrency, quoteCurrency);
            return Result.Failure<decimal>(ExchangeRateErrors.Rejected);
        }

        if (root.ValueKind != JsonValueKind.Array)
        {
            return Result.Failure<decimal>(ExchangeRateErrors.MalformedResponse);
        }

        RateQuote? quote = root.Deserialize<RateQuote[]>(SerializerOptions)?
            .FirstOrDefault(entry => string.Equals(entry.Quote, quoteCurrency, StringComparison.Ordinal));

        return quote switch
        {
            // Asked for, not answered: the feed does not publish that currency.
            null => Result.Failure<decimal>(ExchangeRateErrors.UnknownCurrency(quoteCurrency)),
            { Rate: { } rate } => Result.Success(rate),
            _ => Result.Failure<decimal>(ExchangeRateErrors.MalformedResponse),
        };
    }

    /// <summary>
    /// One element of the provider's <c>/v2/rates</c> answer
    /// (<c>[{"date":"…","base":"EUR","quote":"USD","rate":1.08}]</c>); kept private so the third party's
    /// schema never leaks inward. Only the fields the port needs are read.
    /// </summary>
    private sealed record RateQuote
    {
        [JsonPropertyName("quote")]
        public string? Quote { get; init; }

        [JsonPropertyName("rate")]
        public decimal? Rate { get; init; }
    }
}
