using System.Diagnostics.CodeAnalysis;

namespace Trellis.SharedKernel.Results;

/// <summary>
/// Outcome of an operation that can fail. Failures carry an <see cref="Error"/>; exceptions are reserved for the truly
/// exceptional (ADR-0004). <see cref="Error"/> is null on a success, and the compiler knows it: reading
/// <c>result.Error.Code</c> warns until an <see cref="IsFailure"/> or <see cref="IsSuccess"/> check proves a failure,
/// so an unguarded read cannot compile into a 500 with an empty code.
/// </summary>
public class Result
{
    protected Result(bool isSuccess, Error? error)
    {
        if (isSuccess && error is not null ||
            !isSuccess && error is null)
        {
            throw new ArgumentException("Invalid combination of success state and error.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    [MemberNotNullWhen(false, nameof(Error))]
    public bool IsSuccess { get; }

    [MemberNotNullWhen(true, nameof(Error))]
    public bool IsFailure => !IsSuccess;

    /// <summary>The failure's error; null on a success. Check <see cref="IsFailure"/> first.</summary>
    public Error? Error { get; }

    public static Result Success() => new(true, null);

    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, null);

    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);

    /// <summary>Lets a handler <c>return SomeErrors.X;</c> for a non-generic <see cref="Result"/>, as <see cref="Result{TValue}"/> already allows.</summary>
    public static implicit operator Result(Error error) => Failure(error);

    /// <summary>
    /// Success when every result succeeded; otherwise ONE error, chosen by type. When every failure is a
    /// validation error they are merged into a <see cref="ValidationError"/> (nested ones flattened), so a
    /// caller creating several value objects reports every problem in one 400 instead of one at a time.
    /// When any failure is of another type, merging would turn a 404 or a 500 into a 400, so the first
    /// <see cref="ErrorType.Failure"/> wins, else the first non-validation failure; the validation errors
    /// beside it are dropped. A single failure is returned as it is.
    /// </summary>
    public static Result Combine(params Result[] results)
        => MergeFailures(results) is { } error ? Failure(error) : Success();

    /// <summary>
    /// <see cref="Combine(Result[])"/> that keeps the values: both on success, as a tuple the
    /// deconstructing <c>Bind</c>/<c>Map</c> overloads (<c>(a, b) =&gt; …</c>) unpack.
    /// </summary>
    public static Result<(T1, T2)> Combine<T1, T2>(Result<T1> first, Result<T2> second)
        => MergeFailures([first, second]) is { } error
            ? Failure<(T1, T2)>(error)
            : Success((first.Value, second.Value));

    /// <inheritdoc cref="Combine{T1, T2}(Result{T1}, Result{T2})"/>
    public static Result<(T1, T2, T3)> Combine<T1, T2, T3>(Result<T1> first, Result<T2> second, Result<T3> third)
        => MergeFailures([first, second, third]) is { } error
            ? Failure<(T1, T2, T3)>(error)
            : Success((first.Value, second.Value, third.Value));

    private static Error? MergeFailures(ReadOnlySpan<Result> results)
    {
        List<Error> validation = [];
        Error? first = null;
        Error? decisive = null;
        int failures = 0;

        foreach (Result result in results)
        {
            if (result.IsSuccess)
            {
                continue;
            }

            failures++;
            first ??= result.Error;
            if (result.Error.Type != ErrorType.Validation)
            {
                if (decisive is null || (decisive.Type != ErrorType.Failure && result.Error.Type == ErrorType.Failure))
                {
                    decisive = result.Error;
                }
            }
            else if (result.Error is ValidationError nested)
            {
                validation.AddRange(nested.Errors);
            }
            else
            {
                validation.Add(result.Error);
            }
        }

        return failures switch
        {
            0 => null,
            1 => first,
            _ => decisive ?? new ValidationError(validation),
        };
    }
}

/// <summary>A <see cref="Result"/> carrying a value when successful. Accessing <see cref="Value"/> on a failure throws.</summary>
public class Result<TValue> : Result
{
    protected internal Result(TValue? value, bool isSuccess, Error? error)
        : base(isSuccess, error)
        => Value = value!;

    public TValue Value
    {
        get => IsSuccess
            ? field
            : throw new InvalidOperationException("The value of a failure result cannot be accessed.");
        private init;
    }

    public static implicit operator Result<TValue>(TValue value) => Success(value);

    public static implicit operator Result<TValue>(Error error) => Failure<TValue>(error);
}
