using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.SharedKernel.Decorators;

/// <summary>
/// Commits the unit of work after a successful command (one commit = one transaction) and returns the
/// commit's own failure: a concurrency conflict is a Result like any other expected failure, so it
/// reaches the client as a 409 through the single Result→HTTP seam and is logged as an outcome, not a
/// fault. Applies to commands only — queries never mutate state.
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

            return result.IsSuccess
                ? await unitOfWork.CommitAsync(cancellationToken)
                : result;
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
            if (result.IsFailure)
            {
                return result;
            }

            Result commit = await unitOfWork.CommitAsync(cancellationToken);
            return commit.IsSuccess ? result : Result.Failure<TResponse>(commit.Error);
        }
    }
}
