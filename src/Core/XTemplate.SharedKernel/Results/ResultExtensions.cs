namespace XTemplate.SharedKernel.Results;

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

    /// <summary>Transforms the value on success; the transformation itself cannot fail.</summary>
    public static Result<TOut> Map<TIn, TOut>(this Result<TIn> result, Func<TIn, TOut> map)
        => result.IsFailure ? Result.Failure<TOut>(result.Error) : Result.Success(map(result.Value));

    public static async Task<Result<TOut>> Map<TIn, TOut>(this Task<Result<TIn>> resultTask, Func<TIn, TOut> map)
        => (await resultTask).Map(map);

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
