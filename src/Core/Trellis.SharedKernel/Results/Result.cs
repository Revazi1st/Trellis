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
    /// Success when every result succeeded; otherwise a single error carrying ALL the failures
    /// (nested <see cref="ValidationError"/>s are flattened) — so a caller creating several value
    /// objects reports every problem at once instead of one 400 at a time.
    /// </summary>
    public static Result Combine(params Result[] results)
    {
        List<Error> errors = [];
        foreach (Result result in results)
        {
            if (result.IsFailure)
            {
                if (result.Error is ValidationError validation)
                {
                    errors.AddRange(validation.Errors);
                }
                else
                {
                    errors.Add(result.Error);
                }
            }
        }

        return errors.Count switch
        {
            0 => Success(),
            1 => Failure(errors[0]),
            _ => Failure(new ValidationError(errors)),
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
