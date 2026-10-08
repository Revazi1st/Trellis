using System.Diagnostics;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.SharedKernel.Decorators;

/// <summary>
/// One <see cref="Activity"/> span per use case, nested inside the HTTP span, so a trace shows which
/// use case ran and how its time was spent. Register <see cref="ActivitySourceName"/> with
/// OpenTelemetry in the host. It sits outside Validation, so a rejected request still has its span.
/// Every span carries <see cref="OutcomeTag"/>; an expected failure adds the error code and type but
/// leaves the status unset, and only an exception that ends the use case marks the span as an error
/// and records the exception event.
/// </summary>
public static class TracingDecorators
{
    public const string ActivitySourceName = "Trellis.UseCases";

    /// <summary><c>success</c>, <c>failure</c> (a failed Result), <c>exception</c> or <c>cancelled</c> (the caller gave up).</summary>
    public const string OutcomeTag = "usecase.outcome";

    public const string ErrorCodeTag = "usecase.error_code";

    public const string ErrorTypeTag = "usecase.error_type";

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
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            activity?.AddException(exception);
            throw;
        }
    }
}
