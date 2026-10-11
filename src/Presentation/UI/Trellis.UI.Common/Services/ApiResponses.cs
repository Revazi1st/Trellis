using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Trellis.Api.Contracts.Common;

namespace Trellis.UI.Common.Services;

/// <summary>
/// Turns what the API answered into what a user reads: the one place both typed clients map a failure.
/// <para>
/// Keyed on the problem's stable <c>code</c> first and never on its <c>title</c>, so a policy change that
/// moves a code to another status does not change the sentence. Where a response carries no code yet,
/// the status and the server's <c>detail</c> decide: a 400/401/403/404/409 shows the detail (a 400 with
/// validation errors shows those), a 412 says the resource changed elsewhere, a 429 a retry hint built from
/// <c>Retry-After</c>, a 5xx or a missing response a service-unavailable sentence, and a 401 with nothing to
/// read the invalid-credentials one.
/// </para>
/// </summary>
internal static class ApiResponses
{
    public const string InvalidCredentials = "Invalid email or password.";
    public const string AccountLocked = "This account is temporarily locked. Try again later.";
    public const string SessionExpired = "Your session has expired. Please sign in again.";
    public const string NotSignedIn = "You are not signed in.";
    public const string NoPermission = "You do not have permission to do that.";
    public const string InvalidInput = "Some of the entered values are invalid.";
    public const string TooManyRequests = "Too many requests. Try again shortly.";
    public const string ServiceUnavailable = "The service is unavailable right now. Try again shortly.";
    public const string ChangedElsewhere = "Someone else changed this in the meantime. The latest version is shown now; check it and try again.";

    /// <summary>
    /// The failure for a call the API answered with an error status: its problem body (when readable)
    /// is parsed and kept. A call with no usable answer at all never gets here (<see cref="ApiCall"/>).
    /// </summary>
    public static async Task<ApiResult<T>> FailureFromAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        ProblemResponse? problem = await ReadProblemAsync(response, cancellationToken);
        return ApiResult.Failure<T>(
            Describe(response.StatusCode, problem, RetryAfter(response)),
            (int)response.StatusCode,
            problem);
    }

    /// <summary>
    /// The sentence for a failed call. <paramref name="status"/> is null when no response arrived;
    /// <paramref name="retryAfter"/> is the response's <c>Retry-After</c>, if any.
    /// </summary>
    public static string Describe(HttpStatusCode? status, ProblemResponse? problem, TimeSpan? retryAfter)
        => DescribeCode(problem, retryAfter) ?? DescribeStatus(status, problem, retryAfter);

    private static string? DescribeCode(ProblemResponse? problem, TimeSpan? retryAfter) => problem?.Code switch
    {
        "Auth.InvalidCredentials" => InvalidCredentials,
        "Auth.AccountLockedOut" => DetailOr(problem, AccountLocked),
        "Auth.InvalidRefreshToken" => SessionExpired,
        "Auth.Unauthenticated" => NotSignedIn,
        "Auth.Forbidden" => NoPermission,
        "RateLimit.Exceeded" => RetryHint(retryAfter),
        "Validation.General" => JoinErrors(problem) ?? DetailOr(problem, InvalidInput),
        _ => null,
    };

    private static string DescribeStatus(HttpStatusCode? status, ProblemResponse? problem, TimeSpan? retryAfter)
    {
        // No response at all, or a server-side failure: its detail is opaque by design, so never show it.
        if (status is not { } code || (int)code >= 500)
        {
            return ServiceUnavailable;
        }

        if (code == HttpStatusCode.TooManyRequests)
        {
            return RetryHint(retryAfter);
        }

        // A write that named the version the page loaded (If-Match), and the resource has moved on: the
        // page reloads after every action, so the sentence describes what the user now sees.
        if (code == HttpStatusCode.PreconditionFailed)
        {
            return ChangedElsewhere;
        }

        if (code == HttpStatusCode.BadRequest && JoinErrors(problem) is { } errors)
        {
            return errors;
        }

        // A 4xx problem's detail is written for the caller (the lockout sentence, Identity's password
        // policy, "already exists"): show it rather than substitute a sentence of our own.
        if (!string.IsNullOrWhiteSpace(problem?.Detail))
        {
            return problem.Detail;
        }

        return code switch
        {
            // Bearer-protected endpoints always answer 401 with a problem body, so a 401 with nothing to
            // read is the sign-in endpoint rejecting the credentials.
            HttpStatusCode.Unauthorized => InvalidCredentials,
            HttpStatusCode.Forbidden => NoPermission,
            HttpStatusCode.BadRequest => InvalidInput,
            _ => string.Create(CultureInfo.InvariantCulture, $"Request failed ({(int)code})."),
        };
    }

    private static string DetailOr(ProblemResponse problem, string fallback)
        => string.IsNullOrWhiteSpace(problem.Detail) ? fallback : problem.Detail;

    /// <summary>The validation failures' own sentences, in order; null when there are none.</summary>
    private static string? JoinErrors(ProblemResponse? problem)
    {
        string[] descriptions = problem?.Errors?
            .Select(static error => error.Description)
            .Where(static description => !string.IsNullOrWhiteSpace(description))
            .ToArray() ?? [];

        return descriptions.Length > 0 ? string.Join(' ', descriptions) : null;
    }

    private static string RetryHint(TimeSpan? retryAfter)
    {
        if (retryAfter is not { } wait || wait <= TimeSpan.Zero)
        {
            return TooManyRequests;
        }

        int seconds = (int)Math.Ceiling(wait.TotalSeconds);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"Too many requests. Try again in {seconds} {(seconds == 1 ? "second" : "seconds")}.");
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response) => response.Headers.RetryAfter switch
    {
        { Delta: { } delta } => delta,
        { Date: { } date } => date - DateTimeOffset.UtcNow,
        _ => null,
    };

    private static async Task<ProblemResponse?> ReadProblemAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentType?.MediaType is not ("application/problem+json" or "application/json"))
        {
            return null;
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<ProblemResponse>(cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or NotSupportedException or JsonException)
        {
            // An unreadable body is no reason to fail differently: the status-based sentence still applies.
            return null;
        }
    }
}
