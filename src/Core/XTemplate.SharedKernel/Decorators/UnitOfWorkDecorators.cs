using XTemplate.SharedKernel.Domain;
using XTemplate.SharedKernel.Messaging;
using XTemplate.SharedKernel.Results;

namespace XTemplate.SharedKernel.Decorators;

/// <summary>
/// Commits the unit of work after a successful command (one SaveChanges = one transaction).
/// Applies to commands only — queries never mutate state.
/// </summary>
public static class UnitOfWorkDecorators
{
    public sealed class CommandHandler<TCommand>(
        ICommandHandler<TCommand> inner,
        IUnitOfWork unitOfWork) : ICommandHandler<TCommand>
        where TCommand : ICommand
    {
        public async Task<Result> Handle(TCommand command, CancellationToken cancellationToken)
        {
            Result result = await inner.Handle(command, cancellationToken);

            if (result.IsSuccess)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
    }

    public sealed class CommandHandler<TCommand, TResponse>(
        ICommandHandler<TCommand, TResponse> inner,
        IUnitOfWork unitOfWork) : ICommandHandler<TCommand, TResponse>
        where TCommand : ICommand<TResponse>
    {
        public async Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken)
        {
            Result<TResponse> result = await inner.Handle(command, cancellationToken);

            if (result.IsSuccess)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
    }
}
