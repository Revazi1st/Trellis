using System.Diagnostics;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.SharedKernel.Decorators;

/// <summary>
/// One <see cref="Activity"/> span per use case, nested inside the HTTP span, so a trace shows which
/// use case ran and how its time was spent. ServiceDefaults exports <see cref="ActivitySourceName"/>
/// with every other <c>Trellis.*</c> source. It is the outermost stage: outside Validation, so a rejected
/// request still has its span, and outside Logging, so the outcome line is written inside the span.
/// Every span carries <see cref="OutcomeTag"/>; an expected failure adds the error code and type but
/// leaves the status unset, and only an exception that ends the use case marks the span as an error:
/// <see cref="FaultTypeTag"/>, status Error with the exception's message, and the exception event. The
/// outcome is also where <see cref="UseCaseMetrics"/> counts validation failures.
/// </summary>
public static class TracingDecorators
{
    public const string ActivitySourceName = "Trellis.UseCases";

    /// <summary><c>success</c>, <c>failure</c> (a failed Result), <c>exception</c> or <c>cancelled</c> (the caller gave up).</summary>
    public const string OutcomeTag = "trellis.usecase.outcome";

    /// <summary>A failed Result's <see cref="Error.Code"/>.</summary>
    public const string ErrorCodeTag = "trellis.usecase.error_code";

    /// <summary>
    /// A failed Result's <see cref="ErrorType"/>. Deliberately not <c>error.type</c>: backends count a span
    /// carrying that attribute as an error, and a 404 or a rejected form is an outcome, not a fault.
    /// </summary>
    public const string ErrorTypeTag = "trellis.usecase.error_type";

    /// <summary>
    /// The OpenTelemetry semantic-convention attribute, set only when the use case throws: the exception's
    /// full type name (<c>System.InvalidOperationException</c>).
    /// </summary>
    public const string FaultTypeTag = "error.type";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public sealed class CommandHandler<TCommand>(ICommandHandler<TCommand> inner) : ICommandHandler<TCommand>
        where TCommand : ICommand
    {
        public Task<Result> Handle(TCommand command, CancellationToken cancellationToken)
            => Execute(typeof(TCommand).Name, () => inner.Handle(command, cancellationToken), cancellationToken);
    }

    public sealed class CommandHandler<TCommand, TResponse>(ICommandHandler<TCommand, TResponse> inner)
        : ICommandHandler<TCommand, TResponse>
        where TCommand : ICommand<TResponse>
    {
        public Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken)
            => Execute(typeof(TCommand).Name, () => inner.Handle(command, cancellationToken), cancellationToken);
    }

    public sealed class QueryHandler<TQuery, TResponse>(IQueryHandler<TQuery, TResponse> inner)
        : IQueryHandler<TQuery, TResponse>
        where TQuery : IQuery<TResponse>
    {
        public Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken)
            => Execute(typeof(TQuery).Name, () => inner.Handle(query, cancellationToken), cancellationToken);
    }

    private static async Task<TResult> Execute<TResult>(string useCase, Func<Task<TResult>> handle, CancellationToken cancellationToken)
        where TResult : Result
    {
        using Activity? activity = ActivitySource.StartActivity(useCase);

        try
        {
            TResult result = await handle();
            UseCaseMetrics.RecordOutcome(useCase, result);
            if (result.IsSuccess)
            {
                activity?.SetTag(OutcomeTag, "success");
            }
            else
            {
                activity?.SetTag(OutcomeTag, "failure");
                activity?.SetTag(ErrorCodeTag, result.Error.Code);
                activity?.SetTag(ErrorTypeTag, result.Error.Type.ToString());
            }

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetTag(OutcomeTag, "cancelled");
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Keyed on the caller's token: a downstream timeout's OperationCanceledException is a fault.
            activity?.SetTag(OutcomeTag, "exception");
            activity?.SetTag(FaultTypeTag, exception.GetType().FullName);
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            throw;
        }
    }
}
