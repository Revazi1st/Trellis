using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.SharedKernel.Decorators;

/// <summary>
/// The outermost stage of the use-case pipeline: every invocation writes exactly one outcome line in a
/// <c>UseCase</c> scope that correlates everything the inner stages and the handler log. The level
/// follows the outcome class, because the line is a record of one request's story, not an alarm:
/// <list type="bullet">
/// <item>success, and a classified expected failure (Validation, NotFound, Conflict, Unauthorized,
/// Forbidden): <see cref="LogLevel.Information"/>; a validation failure lists its <c>field:code</c> pairs;</item>
/// <item>an unclassified failure (<see cref="ErrorType.Failure"/>): <see cref="LogLevel.Warning"/>, until
/// someone classifies it;</item>
/// <item>an exception that ends the use case: <see cref="LogLevel.Error"/> — including a downstream
/// <see cref="OperationCanceledException"/> (an HTTP timeout) while the caller's token is still live;</item>
/// <item>the caller's own cancellation (client gone, host stopping): <see cref="LogLevel.Information"/>.</item>
/// </list>
/// Validation and the unit-of-work commit run INSIDE this stage, so a rejected request and a failing
/// commit both still produce their use-case-named line.
/// </summary>
public static class LoggingDecorators
{
    /// <summary>Caps the <c>field:code</c> list so a pathological payload cannot produce a pathological log line.</summary>
    private const int MaxLoggedValidationFailures = 10;

    public sealed class CommandHandler<TCommand>(
        ICommandHandler<TCommand> inner,
        ILogger<CommandHandler<TCommand>> logger) : ICommandHandler<TCommand>
        where TCommand : ICommand
    {
        public Task<Result> Handle(TCommand command, CancellationToken cancellationToken)
            => Execute(logger, typeof(TCommand).Name, () => inner.Handle(command, cancellationToken), cancellationToken);
    }

    public sealed class CommandHandler<TCommand, TResponse>(
        ICommandHandler<TCommand, TResponse> inner,
        ILogger<CommandHandler<TCommand, TResponse>> logger) : ICommandHandler<TCommand, TResponse>
        where TCommand : ICommand<TResponse>
    {
        public Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken)
            => Execute(logger, typeof(TCommand).Name, () => inner.Handle(command, cancellationToken), cancellationToken);
    }

    public sealed class QueryHandler<TQuery, TResponse>(
        IQueryHandler<TQuery, TResponse> inner,
        ILogger<QueryHandler<TQuery, TResponse>> logger) : IQueryHandler<TQuery, TResponse>
        where TQuery : IQuery<TResponse>
    {
        public Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken)
            => Execute(logger, typeof(TQuery).Name, () => inner.Handle(query, cancellationToken), cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S2139:Exceptions should be either logged or rethrown but not both",
        Justification = "This decorator IS the logging boundary: the log line carries the use-case name that is lost by the time the API's exception handler sees the throw.")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S6667:Logging in a catch clause should pass the caught exception as a parameter",
        Justification = "Only the caller-cancelled line omits it: the caller giving up is an outcome, not a fault, and its stack trace is noise. The fault line passes the exception.")]
    private static async Task<TResult> Execute<TResult>(
        ILogger logger, string useCase, Func<Task<TResult>> handle, CancellationToken cancellationToken)
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up: an outcome worth one line, never an error.
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation("Use case {UseCase} was cancelled by the caller after {ElapsedMilliseconds}ms",
                    useCase, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            }

            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Keyed on the caller's token, not the exception type: a downstream timeout surfaces as an
            // OperationCanceledException too, and it is a failure the use case did not survive.
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

            return;
        }

        LogLevel level = IsClassifiedExpectedFailure(result.Error.Type) ? LogLevel.Information : LogLevel.Warning;
        if (logger.IsEnabled(level))
        {
            logger.Log(level, "Use case {UseCase} failed in {ElapsedMilliseconds}ms with {ErrorCode}: {ErrorDetail}",
                useCase, elapsed.TotalMilliseconds, result.Error.Code, DetailOf(result.Error));
        }
    }

    /// <summary>
    /// The failures a caller causes and the API answers with a 4xx: outcomes, not alarms. Everything else —
    /// <see cref="ErrorType.Failure"/>, and any type added later (a transient "unavailable", say) — logs at
    /// Warning until someone classifies it here.
    /// </summary>
    private static bool IsClassifiedExpectedFailure(ErrorType type)
        => type is ErrorType.Validation or ErrorType.NotFound or ErrorType.Conflict or ErrorType.Unauthorized or ErrorType.Forbidden;

    /// <summary>A validation failure names what failed (<c>CustomerName:Validation.NotEmpty, …</c>); anything else its description.</summary>
    private static string DetailOf(Error error)
    {
        if (error is not ValidationError validation)
        {
            return error.Description;
        }

        IEnumerable<string> failures = validation.Errors
            .Take(MaxLoggedValidationFailures)
            .Select(static e => e is FieldValidationFailure field ? field.Field + ":" + field.RuleCode : e.Code);

        string detail = string.Join(", ", failures);
        return validation.Errors.Count > MaxLoggedValidationFailures ? detail + ", …" : detail;
    }
}
