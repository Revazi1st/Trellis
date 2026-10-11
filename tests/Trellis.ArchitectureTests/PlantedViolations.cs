using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.ArchitectureTests;

/// <summary>
/// Deliberately broken shapes for the positive controls: each reflection rule is also run against the
/// shape it exists to refuse, because a rule that passes a clean codebase and a broken check alike proves
/// nothing. Nothing registers these types, and no production rule scans this assembly.
/// </summary>
internal static class PlantedViolations
{
    internal sealed record Command : ICommand;

    /// <summary>A use case that runs two other use cases' whole pipelines and commits by hand, mid-command.</summary>
    internal sealed class HandlerCallingOtherUseCases(
        ICommandHandler<Command> next,
        Func<ICommandHandler<Command>> later,
        IUnitOfWork unitOfWork) : ICommandHandler<Command>
    {
        public async Task<Result> Handle(Command command, CancellationToken cancellationToken)
            => Result.Combine(
                await next.Handle(command, cancellationToken),
                await later().Handle(command, cancellationToken),
                await unitOfWork.CommitAsync(cancellationToken));
    }
}
