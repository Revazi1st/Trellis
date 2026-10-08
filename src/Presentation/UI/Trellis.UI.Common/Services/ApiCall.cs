using System.Net.Http.Json;
using System.Text.Json;

namespace Trellis.UI.Common.Services;

/// <summary>
/// The one way the typed clients make a call: send, then read, and turn every way the call can fail
/// into an <see cref="ApiResult{T}"/> instead of an exception that would reach the page's error boundary.
/// <list type="bullet">
/// <item>An error status: the API's problem, described by <see cref="ApiResponses"/>.</item>
/// <item>No usable answer: no response (connection refused or reset, DNS), a timeout (the client's own
/// <see cref="HttpClient.Timeout"/>, or the host resilience pipeline's attempt or total timeout), an open
/// circuit, or a 2xx whose body is missing, <c>null</c> or not the JSON the contract promises. Each is
/// the service-unavailable sentence with no <see cref="ApiResult{T}.StatusCode"/>.</item>
/// <item>A cancellation the caller asked for propagates: that is the caller's decision, not a failure.</item>
/// </list>
/// Anything else (a bug) still throws.
/// </summary>
internal static class ApiCall
{
    /// <summary>
    /// The base type of every Polly rejection: <c>BrokenCircuitException</c> (the circuit is open),
    /// <c>TimeoutRejectedException</c> (an attempt or the whole call timed out) and
    /// <c>RateLimiterRejectedException</c>. UI.Common does not reference Polly: the host owns the resilience
    /// pipeline (ServiceDefaults' standard handler), so the rejection is recognised by this name.
    /// </summary>
    private const string ResilienceRejection = "Polly.ExecutionRejectedException";

    /// <summary>A call whose success carries a JSON body of <typeparamref name="TBody"/>.</summary>
    public static Task<ApiResult<TBody>> ReadAsync<TBody>(
        Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
        where TBody : class
        => ReadAsync<TBody, TBody>(send, static body => body, cancellationToken);

    /// <summary>A call whose success carries a JSON body of <typeparamref name="TBody"/>, of which the caller keeps <paramref name="select"/>.</summary>
    public static async Task<ApiResult<TResult>> ReadAsync<TBody, TResult>(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        Func<TBody, TResult> select,
        CancellationToken cancellationToken)
        where TBody : class
    {
        try
        {
            using HttpResponseMessage response = await send(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return await ApiResponses.FailureFromAsync<TResult>(response, cancellationToken);
            }

            // An empty body throws JsonException below; a literal null deserialises to null.
            TBody? body = await response.Content.ReadFromJsonAsync<TBody>(cancellationToken);
            return body is null ? Unavailable<TResult>() : ApiResult.Success(select(body));
        }
        catch (Exception exception) when (IsUnusableAnswer(exception, cancellationToken))
        {
            return Unavailable<TResult>();
        }
    }

    /// <summary>A call whose success is the status alone (a 204, or a body the caller has no use for).</summary>
    public static async Task<ApiResult<bool>> SendAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await send(cancellationToken);
            return response.IsSuccessStatusCode
                ? ApiResult.Success(true)
                : await ApiResponses.FailureFromAsync<bool>(response, cancellationToken);
        }
        catch (Exception exception) when (IsUnusableAnswer(exception, cancellationToken))
        {
            return Unavailable<bool>();
        }
    }

    private static ApiResult<T> Unavailable<T>() => ApiResult.Failure<T>(ApiResponses.ServiceUnavailable);

    /// <summary>
    /// True for the failures that mean "no usable answer arrived". A cancellation counts only when the
    /// caller's token did not ask for it: then it is a timeout (HttpClient reports its own as a
    /// TaskCanceledException nobody asked for).
    /// </summary>
    private static bool IsUnusableAnswer(Exception exception, CancellationToken cancellationToken) => exception switch
    {
        OperationCanceledException => !cancellationToken.IsCancellationRequested,
        HttpRequestException or JsonException => true,
        // ReadFromJsonAsync refuses a 2xx whose content type is not JSON (an HTML page from a proxy).
        NotSupportedException => true,
        _ => IsResilienceRejection(exception),
    };

    private static bool IsResilienceRejection(Exception exception)
    {
        for (Type? type = exception.GetType(); type is not null; type = type.BaseType)
        {
            if (string.Equals(type.FullName, ResilienceRejection, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
