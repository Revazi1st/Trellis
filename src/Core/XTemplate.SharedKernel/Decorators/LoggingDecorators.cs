using Microsoft.Extensions.Logging;
using XTemplate.SharedKernel.Messaging;
using XTemplate.SharedKernel.Results;

namespace XTemplate.SharedKernel.Decorators;

/// <summary>Structured start/success/failure logging around every use case.</summary>
public static class LoggingDecorators
{
    public sealed class CommandHandler<TCommand>(
        ICommandHandler<TCommand> inner,
        ILogger<CommandHandler<TCommand>> logger) : ICommandHandler<TCommand>
        where TCommand : ICommand
    {
        public async Task<Result> Handle(TCommand command, CancellationToken cancellationToken)
        {
            Result result = await inner.Handle(command, cancellationToken);
            Log(logger, typeof(TCommand).Name, result);
            return result;
        }
    }

    public sealed class CommandHandler<TCommand, TResponse>(
        ICommandHandler<TCommand, TResponse> inner,
        ILogger<CommandHandler<TCommand, TResponse>> logger) : ICommandHandler<TCommand, TResponse>
        where TCommand : ICommand<TResponse>
    {
        public async Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken)
        {
            Result<TResponse> result = await inner.Handle(command, cancellationToken);
            Log(logger, typeof(TCommand).Name, result);
            return result;
        }
    }

    public sealed class QueryHandler<TQuery, TResponse>(
        IQueryHandler<TQuery, TResponse> inner,
        ILogger<QueryHandler<TQuery, TResponse>> logger) : IQueryHandler<TQuery, TResponse>
        where TQuery : IQuery<TResponse>
    {
        public async Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken)
        {
            Result<TResponse> result = await inner.Handle(query, cancellationToken);
            Log(logger, typeof(TQuery).Name, result);
            return result;
        }
    }

    private static void Log(ILogger logger, string useCase, Result result)
    {
        if (result.IsSuccess)
        {
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Use case {UseCase} succeeded", useCase);
            }
        }
        else if (logger.IsEnabled(LogLevel.Warning))
        {
            logger.LogWarning("Use case {UseCase} failed with {ErrorCode}: {ErrorDescription}",
                useCase, result.Error.Code, result.Error.Description);
        }
    }
}
