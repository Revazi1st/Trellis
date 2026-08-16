namespace Trellis.UI.Common.Services;

/// <summary>A minimal success/failure wrapper for API calls, so components handle expected failures without exceptions.</summary>
public sealed class ApiResult<T>
{
    internal ApiResult(bool isSuccess, T? value, string? error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    public bool IsSuccess { get; }

    public T? Value { get; }

    public string? Error { get; }
}

/// <summary>Factories for <see cref="ApiResult{T}"/> (non-generic to keep the static members off the generic type).</summary>
public static class ApiResult
{
    public static ApiResult<T> Success<T>(T value) => new(true, value, null);

    public static ApiResult<T> Failure<T>(string error) => new(false, default, error);
}
