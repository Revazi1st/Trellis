using FluentValidation;
using Microsoft.Extensions.Logging;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.ArchitectureTests;

/// <summary>
/// Deliberately broken shapes for the positive controls: each reflection rule is also run against the
/// shape it exists to refuse, because a rule that passes a clean codebase and a broken check alike proves
/// nothing; where a rule could over-reach, the nearest shape it must accept sits beside them. Nothing
/// registers these types, and no production rule scans this assembly.
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

    /// <summary>The planted aggregate's strongly-typed id, shaped like the real ones.</summary>
    internal readonly record struct PlantedId(Guid Value) : IStronglyTypedId<PlantedId>
    {
        public static PlantedId From(Guid value) => new(value);
    }

    /// <summary>An aggregate anyone can build unchecked: a public constructor, and no <c>Create</c> factory returning a Result.</summary>
    internal sealed class AggregateWithoutFactory(PlantedId id) : AggregateRoot<PlantedId>(id);

    /// <summary>An aggregate with its Result factory and a second way in that hands it back bare, unchecked.</summary>
    internal sealed class AggregateWithBareFactory : AggregateRoot<PlantedId>
    {
        private AggregateWithBareFactory(PlantedId id)
            : base(id)
        {
        }

        public static Result<AggregateWithBareFactory> Create(PlantedId id) => new AggregateWithBareFactory(id);

        public static AggregateWithBareFactory FromSnapshot(PlantedId id) => new(id);
    }

    /// <summary>
    /// The shape the factory rule accepts: an aggregate only its parent creates, through an internal
    /// <c>Create</c> the parent's own <c>Result</c>-returning method calls.
    /// </summary>
    internal sealed class AggregateCreatedByItsParent : AggregateRoot<PlantedId>
    {
        private AggregateCreatedByItsParent(PlantedId id)
            : base(id)
        {
        }

        internal static Result<AggregateCreatedByItsParent> Create(PlantedId id) => new AggregateCreatedByItsParent(id);
    }
}
