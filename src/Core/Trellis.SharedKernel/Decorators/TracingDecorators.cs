using System.Diagnostics;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.SharedKernel.Decorators;

/// <summary>
/// One <see cref="Activity"/> span per use case, nested inside the HTTP span, so a trace shows
/// which use case ran and how its time was spent. Register <see cref="ActivitySourceName"/> with
/// OpenTelemetry in the host. Expected failures mark the span with the error code; only genuinely
/// unexpected exceptions mark it as an error.
/// </summary>
public static class TracingDecorators
{
    public const string ActivitySourceName = "Trellis.UseCases";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public sealed class CommandHandler<TCommand>(ICommandHandler<TCommand> inner) : ICommandHandler<TCommand>
        where TCommand : ICommand
    {
        public Task<Result> Handle(TCommand command, CancellationToken cancellationToken)
            => Execute(typeof(TCommand).Name, () => inner.Handle(command, cancellationToken));
    }

    public sealed class CommandHandler<TCommand, TResponse>(ICommandHandler<TCommand, TResponse> inner)
        : ICommandHandler<TCommand, TResponse>
        where TCommand : ICommand<TResponse>
    {
        public Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken)
            => Execute(typeof(TCommand).Name, () => inner.Handle(command, cancellationToken));
    }

    public sealed class QueryHandler<TQuery, TResponse>(IQueryHandler<TQuery, TResponse> inner)
        : IQueryHandler<TQuery, TResponse>
        where TQuery : IQuery<TResponse>
    {
        public Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken)
            => Execute(typeof(TQuery).Name, () => inner.Handle(query, cancellationToken));
    }

    private static async Task<TResult> Execute<TResult>(string useCase, Func<Task<TResult>> handle)
        where TResult : Result
    {
        using Activity? activity = ActivitySource.StartActivity(useCase);

        try
        {
            TResult result = await handle();
            if (result.IsFailure)
            {
                activity?.SetTag("usecase.error_code", result.Error.Code);
            }

            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            throw;
        }
    }
}
