using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Trellis.Api.Common.Http;
using Trellis.Api.Common.Results;
using Trellis.Api.Contracts.Common;
using Trellis.Application.Abstractions;

namespace Trellis.Api.Common.Idempotency;

/// <summary>
/// Opt-in command idempotency (ADR-0018). A request carrying an <c>Idempotency-Key</c> header is
/// executed at most once per caller, method and route, and a retry is answered with what the first
/// execution answered:
/// <list type="bullet">
/// <item>No reservation yet: one is tracked in the request's unit of work and the handler runs. The
/// command's commit persists the reservation with the aggregate and the outbox — a command that fails
/// leaves nothing behind, and two concurrent firsts resolve at the primary key (the loser's commit is
/// <see cref="IdempotencyErrors.Duplicate"/>, 409). Once the response has gone out its status, content
/// type, body (up to the endpoint's replay cap) and <c>Location</c> are stored against the reservation.</item>
/// <item>Completed, same request (method, path and body fingerprint): the stored response is replayed
/// byte for byte with <c>Idempotency-Replayed: true</c>. Over the cap, or ended by a fault after the
/// commit, there is no body to replay and the retry is a 409 <see cref="IdempotencyErrors.Duplicate"/>
/// pointing the client at the resource.</item>
/// <item>Completed, different request: 422 <see cref="IdempotencyProblems.FingerprintMismatch"/> — the
/// key was reused, which is a client bug, not a retry.</item>
/// <item>In flight: 409 <see cref="IdempotencyErrors.InFlight"/> with <c>Retry-After</c>.</item>
/// </list>
/// A missing header means "no idempotency"; a blank one is a 400, never a silent opt-out. Everything an
/// endpoint does after its command succeeded is post-commit work and takes
/// <see cref="CancellationToken.None"/>: a fault there ends the request with a 500 but the reservation
/// is already committed, so the retry is a 409, never a second apply. The filter's own outcomes are
/// <see cref="IdempotencyErrors"/> written through <see cref="CustomResults"/>, like any endpoint's.
/// </summary>
public sealed class IdempotencyFilter : IEndpointFilter
{
    public const string HeaderName = ApiHeaders.IdempotencyKey;

    /// <summary>Response header, <c>true</c> on a replayed response: the stored outcome of an earlier request, not a new execution.</summary>
    public const string ReplayedHeaderName = "Idempotency-Replayed";

    public const int MaxKeyLength = 128;

    /// <summary>The largest response body stored for replay unless <c>RequireIdempotency(maxReplayBodyBytes:)</c> says otherwise; 64 KB covers every command response this template ships.</summary>
    public const int DefaultMaxReplayBodyBytes = 64 * 1024;

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        HttpContext http = context.HttpContext;

        if (!http.Request.Headers.TryGetValue(HeaderName, out var header))
        {
            return await next(context);
        }

        string clientKey = header.ToString();
        if (string.IsNullOrWhiteSpace(clientKey))
        {
            // The caller asked for the guarantee and sent nothing usable: running without it would turn
            // the caller's retry into a second apply.
            return CustomResults.Problem(IdempotencyErrors.BlankKey);
        }

        if (clientKey.Length > MaxKeyLength)
        {
            return CustomResults.Problem(IdempotencyErrors.KeyTooLong(MaxKeyLength));
        }

        string key = KeyOf(http, clientKey);
        string fingerprint = RequestFingerprint.Of(context);
        var store = http.RequestServices.GetRequiredService<IIdempotencyStore>();

        if (await AnswerFromStoreAsync(http, store, key, fingerprint) is { } answer)
        {
            return answer;
        }

        store.Reserve(key, fingerprint);
        object? result;
        try
        {
            result = await next(context);
        }
        catch (Exception)
        {
            // Before the commit the reservation was never written and the store drops it; after the
            // commit (post-commit work that threw despite the rule above) the command applied, so the
            // reservation is completed without a response and the retry is a 409, not a re-run.
            await CompletingResult.RecordAsync(http, store, key, StoredResponse.Unavailable(StatusCodes.Status500InternalServerError));
            throw;
        }

        int maxReplayBodyBytes = http.GetEndpoint()?.Metadata.GetMetadata<IdempotencyMetadata>()?.MaxReplayBodyBytes
            ?? DefaultMaxReplayBodyBytes;
        return new CompletingResult(result, key, store, maxReplayBodyBytes);
    }

    /// <summary>
    /// Hashing the (user, method, route, key) composite gives the stored key a fixed length and keeps the
    /// caller's raw value out of the database.
    /// </summary>
    private static string KeyOf(HttpContext http, string clientKey)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{http.RequestServices.GetRequiredService<ICurrentUser>().UserId?.ToString() ?? "anonymous"}:{http.Request.Method}:{http.Request.Path}:{clientKey}")));

    /// <summary>The answer owed to a key the store already knows, or null when the request is a genuine first.</summary>
    private static async Task<object?> AnswerFromStoreAsync(HttpContext http, IIdempotencyStore store, string key, string fingerprint)
    {
        switch (await store.FindAsync(key, http.RequestAborted))
        {
            case IdempotencyLookup.Completed completed when !string.Equals(completed.RequestFingerprint, fingerprint, StringComparison.Ordinal):
                await http.WriteProblemAsync(IdempotencyProblems.FingerprintMismatch);
                return TypedResults.Empty;

            case IdempotencyLookup.Completed { Response.IsReplayable: true } completed:
                return new ReplayedResult(completed.Response);

            case IdempotencyLookup.Completed:
                return CustomResults.Problem(IdempotencyErrors.Duplicate);

            case IdempotencyLookup.InFlight:
                http.Response.Headers.RetryAfter = "1";
                return CustomResults.Problem(IdempotencyErrors.InFlight);

            default:
                return null;
        }
    }
}

/// <summary>Endpoint marker: the OpenAPI transformer documents the header and the outcomes from it, and the filter reads the endpoint's replay cap.</summary>
public sealed class IdempotencyMetadata(int maxReplayBodyBytes)
{
    /// <summary>The largest response body this endpoint stores for replay; a larger response completes but a retry gets a 409.</summary>
    public int MaxReplayBodyBytes { get; } = maxReplayBodyBytes;
}

public static class IdempotencyExtensions
{
    /// <summary>
    /// Applies opt-in idempotency to a state-changing endpoint. Raise <paramref name="maxReplayBodyBytes"/>
    /// only on the endpoint whose response needs it, the way the request-body cap is raised per endpoint.
    /// </summary>
    public static RouteHandlerBuilder RequireIdempotency(
        this RouteHandlerBuilder builder,
        int maxReplayBodyBytes = IdempotencyFilter.DefaultMaxReplayBodyBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxReplayBodyBytes);
        return builder
            .AddEndpointFilter<IdempotencyFilter>()
            .WithMetadata(new IdempotencyMetadata(maxReplayBodyBytes));
    }
}
