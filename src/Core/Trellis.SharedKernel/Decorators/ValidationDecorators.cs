using FluentValidation;
using FluentValidation.Results;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.SharedKernel.Decorators;

/// <summary>
/// Runs every registered FluentValidation validator for the message; short-circuits to a
/// <see cref="ValidationError"/> without invoking the handler (or, for a command, the unit of work).
/// Sits inside Logging and Tracing, so a rejected request still gets its outcome line and its span.
/// </summary>
public static class ValidationDecorators
{
    public sealed class CommandHandler<TCommand>(
        ICommandHandler<TCommand> inner,
        IEnumerable<IValidator<TCommand>> validators) : ICommandHandler<TCommand>
        where TCommand : ICommand
    {
        public async Task<Result> Handle(TCommand command, CancellationToken cancellationToken)
        {
            ValidationError? validationError = await Validate(command, validators, cancellationToken);

            return validationError is null
                ? await inner.Handle(command, cancellationToken)
                : Result.Failure(validationError);
        }
    }

    public sealed class CommandHandler<TCommand, TResponse>(
        ICommandHandler<TCommand, TResponse> inner,
        IEnumerable<IValidator<TCommand>> validators) : ICommandHandler<TCommand, TResponse>
        where TCommand : ICommand<TResponse>
    {
        public async Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken)
        {
            ValidationError? validationError = await Validate(command, validators, cancellationToken);

            return validationError is null
                ? await inner.Handle(command, cancellationToken)
                : Result.Failure<TResponse>(validationError);
        }
    }

    public sealed class QueryHandler<TQuery, TResponse>(
        IQueryHandler<TQuery, TResponse> inner,
        IEnumerable<IValidator<TQuery>> validators) : IQueryHandler<TQuery, TResponse>
        where TQuery : IQuery<TResponse>
    {
        public async Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken)
        {
            ValidationError? validationError = await Validate(query, validators, cancellationToken);

            return validationError is null
                ? await inner.Handle(query, cancellationToken)
                : Result.Failure<TResponse>(validationError);
        }
    }

    private static async Task<ValidationError?> Validate<TMessage>(
        TMessage message,
        IEnumerable<IValidator<TMessage>> validators,
        CancellationToken cancellationToken)
    {
        ValidationFailure[] failures = [];

        foreach (IValidator<TMessage> validator in validators)
        {
            ValidationResult result = await validator.ValidateAsync(message, cancellationToken);

            if (!result.IsValid)
            {
                failures = [.. failures, .. result.Errors];
            }
        }

        return failures.Length == 0
            ? null
            : new ValidationError([.. failures.Select(static f => new FieldError(f.PropertyName, CodeFor(f), f.ErrorMessage))]);
    }

    /// <summary>
    /// FluentValidation's <c>ErrorCode</c> defaults to the validator type name; strip the suffix so the
    /// wire says <c>Validation.NotEmpty</c>, let an explicit <c>.WithErrorCode("Invoice.DueDateInPast")</c>
    /// through verbatim, and name a failure without any code <c>Validation.Invalid</c>.
    /// </summary>
    internal static string CodeFor(ValidationFailure failure)
    {
        const string BuiltInSuffix = "Validator";
        string? code = failure.ErrorCode;

        if (string.IsNullOrEmpty(code))
        {
            return "Validation.Invalid";
        }

        return code.EndsWith(BuiltInSuffix, StringComparison.Ordinal) && code.Length > BuiltInSuffix.Length
            ? "Validation." + code[..^BuiltInSuffix.Length]
            : code;
    }
}
