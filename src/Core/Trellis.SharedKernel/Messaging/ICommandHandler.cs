using Trellis.SharedKernel.Results;

namespace Trellis.SharedKernel.Messaging;

/// <summary>Hand-rolled CQRS (ADR-0003): endpoints inject these interfaces directly — there is no mediator.</summary>
public interface ICommandHandler<in TCommand>
    where TCommand : ICommand
{
    Task<Result> Handle(TCommand command, CancellationToken cancellationToken);
}

public interface ICommandHandler<in TCommand, TResponse>
    where TCommand : ICommand<TResponse>
{
    Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken);
}
