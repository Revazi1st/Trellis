using System.Diagnostics;
using Microsoft.Extensions.Logging;
using XTemplate.SharedKernel.Messaging;
using XTemplate.SharedKernel.Results;

namespace XTemplate.SharedKernel.Decorators;

/// <summary>
/// Structured outcome logging around every use case: success/failure with elapsed time, a scope
/// correlating everything the handler logs, and — because SaveChanges runs in the UnitOfWork
/// decorator INSIDE this one — a log line even when the use case throws (previously a DB failure
/// escaped with no use-case-named telemetry at all).
/// </summary>
public static class LoggingDecorators
{
    public sealed class CommandHandler<TCommand>(
        ICommandHandler<TCommand> inner,
        ILogger<CommandHandler<TCommand>> logger) : ICommandHandler<TCommand>
        where TCommand : ICommand
    {
        public Task<Result> Handle(TCommand command, CancellationToken cancellationToken)
            => Execute(logger, typeof(TCommand).Name, () => inner.Handle(command, cancellationToken));
    }

    public sealed class CommandHandler<TCommand, TResponse>(
        ICommandHandler<TCommand, TResponse> inner,
        ILogger<CommandHandler<TCommand, TResponse>> logger) : ICommandHandler<TCommand, TResponse>
        where TCommand : ICommand<TResponse>
    {
        public Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken)
            => Execute(logger, typeof(TCommand).Name, () => inner.Handle(command, cancellationToken));
    }

    public sealed class QueryHandler<TQuery, TResponse>(
        IQueryHandler<TQuery, TResponse> inner,
        ILogger<QueryHandler<TQuery, TResponse>> logger) : IQueryHandler<TQuery, TResponse>
        where TQuery : IQuery<TResponse>
    {
        public Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken)
            => Execute(logger, typeof(TQuery).Name, () => inner.Handle(query, cancellationToken));
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S2139:Exceptions should be either logged or rethrown but not both",
        Justification = "This decorator IS the logging boundary: the log line carries the use-case name that is lost by the time the API's exception handler sees the throw.")]
    private static async Task<TResult> Execute<TResult>(ILogger logger, string useCase, Func<Task<TResult>> handle)
        where TResult : Result
    {
        using IDisposable? scope = logger.BeginScope("UseCase {UseCase}", useCase);
        long started = Stopwatch.GetTimestamp();

        try
        {
            TResult result = await handle();
            Log(logger, useCase, result, Stopwatch.GetElapsedTime(started));
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Use case {UseCase} threw after {ElapsedMilliseconds}ms",
                useCase, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }

    private static void Log(ILogger logger, string useCase, Result result, TimeSpan elapsed)
    {
        if (result.IsSuccess)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Use case {UseCase} succeeded in {ElapsedMilliseconds}ms",
                    useCase, elapsed.TotalMilliseconds);
            }
        }
        else if (logger.IsEnabled(LogLevel.Warning))
        {
            logger.LogWarning("Use case {UseCase} failed in {ElapsedMilliseconds}ms with {ErrorCode}: {ErrorDescription}",
                useCase, elapsed.TotalMilliseconds, result.Error.Code, result.Error.Description);
        }
    }
}
