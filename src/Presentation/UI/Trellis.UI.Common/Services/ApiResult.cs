using Trellis.Api.Contracts.Common;

namespace Trellis.UI.Common.Services;

/// <summary>
/// A minimal success/failure wrapper for API calls, so components handle expected failures without exceptions.
/// A failure carries the sentence to show (<see cref="Error"/>) and what the API actually answered:
/// <see cref="StatusCode"/>, the parsed <see cref="Problem"/> and its validation <see cref="Errors"/>.
/// </summary>
public sealed class ApiResult<T>
{
    internal ApiResult(bool isSuccess, T? value, string? error, int? statusCode, ProblemResponse? problem)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
        StatusCode = statusCode;
        Problem = problem;
        Errors = problem?.Errors ?? [];
    }

    public bool IsSuccess { get; }

    public T? Value { get; }

    /// <summary>The user-facing sentence describing the failure; null on success.</summary>
    public string? Error { get; }

    /// <summary>
    /// The error status the API answered a failed call with. Null on success, and on a failure where no
    /// usable answer arrived: no response (connection refused or reset, DNS failure), a timeout, an open
    /// circuit, or a 2xx whose body was missing or unreadable.
    /// </summary>
    public int? StatusCode { get; }

    /// <summary>The RFC 9457 problem the API answered with, when the failure carried a readable one.</summary>
    public ProblemResponse? Problem { get; }

    /// <summary>The problem's validation failures; empty (never null) when there are none.</summary>
    public IReadOnlyList<FieldError> Errors { get; }
}

/// <summary>Factories for <see cref="ApiResult{T}"/> (non-generic to keep the static members off the generic type).</summary>
public static class ApiResult
{
    public static ApiResult<T> Success<T>(T value) => new(true, value, null, null, null);

    public static ApiResult<T> Failure<T>(string error, int? statusCode = null, ProblemResponse? problem = null)
        => new(false, default, error, statusCode, problem);
}
