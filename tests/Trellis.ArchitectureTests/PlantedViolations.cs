using FluentValidation;
using Microsoft.Extensions.Logging;
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

    /// <summary>
    /// A domain-shaped rule written as a FluentValidation validator: the SharedKernel references FluentValidation,
    /// so the Domain compiles against it, and only the dependency rule keeps it out.
    /// </summary>
    internal sealed class ValidatorInTheDomain : AbstractValidator<Command>;

    /// <summary>A domain-shaped type that logs: Microsoft.Extensions.Logging reaches the Domain through the SharedKernel too.</summary>
    internal sealed class LoggerInTheDomain(ILogger<LoggerInTheDomain> logger)
    {
        public ILogger Logger { get; } = logger;
    }

    /// <summary>A value object whose factory a positional constructor and init accessors let callers bypass.</summary>
    internal sealed record PositionalValueObject(string Value)
    {
        public static Result<PositionalValueObject> Create(string value)
            => string.IsNullOrWhiteSpace(value)
                ? Error.Validation("Planted.Empty", "A value is required.")
                : new PositionalValueObject(value);
    }
}
