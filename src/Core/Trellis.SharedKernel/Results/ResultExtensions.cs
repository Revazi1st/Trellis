namespace Trellis.SharedKernel.Results;

/// <summary>
/// Railway combinators so handlers read as the pipeline they are instead of ladders of
/// <c>IsFailure</c> checks. The reference usage is CreateInvoiceCommandHandler.
/// </summary>
public static class ResultExtensions
{
    public static TOut Match<TOut>(
        this Result result,
        Func<TOut> onSuccess,
        Func<Error, TOut> onFailure)
        => result.IsSuccess ? onSuccess() : onFailure(result.Error);

    public static TOut Match<TIn, TOut>(
        this Result<TIn> result,
        Func<TIn, TOut> onSuccess,
        Func<Error, TOut> onFailure)
        => result.IsSuccess ? onSuccess(result.Value) : onFailure(result.Error);

    /// <summary>Chains a dependent operation that can itself fail; a failure short-circuits.</summary>
    public static Result<TOut> Bind<TIn, TOut>(this Result<TIn> result, Func<TIn, Result<TOut>> bind)
        => result.IsFailure ? Result.Failure<TOut>(result.Error) : bind(result.Value);

    public static Result Bind<TIn>(this Result<TIn> result, Func<TIn, Result> bind)
        => result.IsFailure ? Result.Failure(result.Error) : bind(result.Value);

    public static async Task<Result<TOut>> Bind<TIn, TOut>(this Result<TIn> result, Func<TIn, Task<Result<TOut>>> bind)
        => result.IsFailure ? Result.Failure<TOut>(result.Error) : await bind(result.Value);

    public static async Task<Result> Bind<TIn>(this Result<TIn> result, Func<TIn, Task<Result>> bind)
        => result.IsFailure ? Result.Failure(result.Error) : await bind(result.Value);

    public static async Task<Result<TOut>> Bind<TIn, TOut>(this Task<Result<TIn>> resultTask, Func<TIn, Result<TOut>> bind)
        => (await resultTask).Bind(bind);

    public static async Task<Result> Bind<TIn>(this Task<Result<TIn>> resultTask, Func<TIn, Result> bind)
        => (await resultTask).Bind(bind);

    public static async Task<Result<TOut>> Bind<TIn, TOut>(this Task<Result<TIn>> resultTask, Func<TIn, Task<Result<TOut>>> bind)
        => await (await resultTask).Bind(bind);

    /// <summary>
    /// <see cref="Bind{TIn, TOut}(Result{TIn}, Func{TIn, Result{TOut}})"/> over a <c>Result.Combine(a, b)</c>
    /// tuple, unpacked into the lambda's parameters: <c>.Bind((email, currency) =&gt; Invoice.Create(…))</c>.
    /// </summary>
    public static Result<TOut> Bind<T1, T2, TOut>(this Result<(T1, T2)> result, Func<T1, T2, Result<TOut>> bind)
        => result.IsFailure ? Result.Failure<TOut>(result.Error) : bind(result.Value.Item1, result.Value.Item2);

    /// <inheritdoc cref="Bind{T1, T2, TOut}(Result{ValueTuple{T1, T2}}, Func{T1, T2, Result{TOut}})"/>
    public static Result<TOut> Bind<T1, T2, T3, TOut>(this Result<(T1, T2, T3)> result, Func<T1, T2, T3, Result<TOut>> bind)
        => result.IsFailure
            ? Result.Failure<TOut>(result.Error)
            : bind(result.Value.Item1, result.Value.Item2, result.Value.Item3);

    /// <summary>Transforms the value on success; the transformation itself cannot fail.</summary>
    public static Result<TOut> Map<TIn, TOut>(this Result<TIn> result, Func<TIn, TOut> map)
        => result.IsFailure ? Result.Failure<TOut>(result.Error) : Result.Success(map(result.Value));

    public static async Task<Result<TOut>> Map<TIn, TOut>(this Task<Result<TIn>> resultTask, Func<TIn, TOut> map)
        => (await resultTask).Map(map);

    /// <summary><see cref="Map{TIn, TOut}(Result{TIn}, Func{TIn, TOut})"/> over a <c>Result.Combine(a, b)</c> tuple, unpacked into the lambda's parameters.</summary>
    public static Result<TOut> Map<T1, T2, TOut>(this Result<(T1, T2)> result, Func<T1, T2, TOut> map)
        => result.IsFailure ? Result.Failure<TOut>(result.Error) : Result.Success(map(result.Value.Item1, result.Value.Item2));

    /// <inheritdoc cref="Map{T1, T2, TOut}(Result{ValueTuple{T1, T2}}, Func{T1, T2, TOut})"/>
    public static Result<TOut> Map<T1, T2, T3, TOut>(this Result<(T1, T2, T3)> result, Func<T1, T2, T3, TOut> map)
        => result.IsFailure
            ? Result.Failure<TOut>(result.Error)
            : Result.Success(map(result.Value.Item1, result.Value.Item2, result.Value.Item3));

    /// <summary>
    /// Replaces a failure's error and passes a success through — e.g. collapsing every lookup failure into
    /// one opaque code before the next step, so that step's own failures stay distinct.
    /// </summary>
    public static Result<TIn> MapOnFailure<TIn>(this Result<TIn> result, Func<Error, Error> map)
        => result.IsFailure ? Result.Failure<TIn>(map(result.Error)) : result;

    public static async Task<Result<TIn>> MapOnFailure<TIn>(this Task<Result<TIn>> resultTask, Func<Error, Error> map)
        => (await resultTask).MapOnFailure(map);

    /// <summary>Runs a side effect on success (e.g. repository Add) and passes the result through.</summary>
    public static Result<TIn> Tap<TIn>(this Result<TIn> result, Action<TIn> action)
    {
        if (result.IsSuccess)
        {
            action(result.Value);
        }

        return result;
    }

    public static async Task<Result<TIn>> Tap<TIn>(this Task<Result<TIn>> resultTask, Action<TIn> action)
        => (await resultTask).Tap(action);

    /// <summary>Turns a success into the given failure when the predicate rejects the value.</summary>
    public static Result<TIn> Ensure<TIn>(this Result<TIn> result, Func<TIn, bool> predicate, Error error)
        => result.IsSuccess && !predicate(result.Value) ? Result.Failure<TIn>(error) : result;
}
