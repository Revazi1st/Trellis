using System.Diagnostics.CodeAnalysis;

namespace XTemplate.SharedKernel.Results;

/// <summary>Outcome of an operation that can fail. Failures carry an <see cref="Error"/>; exceptions are reserved for the truly exceptional (ADR-0004).</summary>
public class Result
{
    protected Result(bool isSuccess, Error error)
    {
        if (isSuccess && error != Error.None ||
            !isSuccess && error == Error.None)
        {
            throw new ArgumentException("Invalid combination of success state and error.", nameof(error));
        }

        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    [MemberNotNullWhen(false, nameof(IsSuccess))]
    public bool IsFailure => !IsSuccess;

    public Error Error { get; }

    public static Result Success() => new(true, Error.None);

    public static Result<TValue> Success<TValue>(TValue value) => new(value, true, Error.None);

    public static Result Failure(Error error) => new(false, error);

    public static Result<TValue> Failure<TValue>(Error error) => new(default, false, error);

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
    protected internal Result(TValue? value, bool isSuccess, Error error)
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
