using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;

namespace Trellis.SharedKernel.Decorators;

/// <summary>
/// The use-case pipeline's outcome record, just inside <see cref="TracingDecorators"/>: every invocation
/// writes exactly one outcome line in a <c>UseCase</c> scope that correlates everything the inner stages
/// and the handler log. Tracing wraps this stage, so the line is written while the use-case span is
/// current and carries its trace and span ids. The level follows the outcome class, because the line is
/// a record of one request's story, not an alarm:
/// <list type="bullet">
/// <item>success, and a classified expected failure (Validation, NotFound, Conflict, Unauthorized,
/// Forbidden, PreconditionFailed): <see cref="LogLevel.Information"/>;</item>
/// <item>an unclassified failure (<see cref="ErrorType.Failure"/>) and a dependency that is down
/// (<see cref="ErrorType.Unavailable"/>): <see cref="LogLevel.Warning"/>;</item>
/// <item>an exception that ends the use case: <see cref="LogLevel.Error"/> — including a downstream
/// <see cref="OperationCanceledException"/> (an HTTP timeout) while the caller's token is still live;</item>
/// <item>the caller's own cancellation (client gone, host stopping): <see cref="LogLevel.Information"/>.</item>
/// </list>
/// A failure line names the error's type and code, and a <see cref="ValidationError"/> its
/// <c>field:code</c> pairs; never the description, which can carry what a user typed, unless
/// <see cref="UseCaseLoggingOptions.IncludeErrorDetail"/> is on (Development). Each kind of line has a
/// stable event id (1001–1006) for alerts and queries to match on.
/// Validation and the unit-of-work commit run INSIDE this stage, so a rejected request and a failing
/// commit both still produce their use-case-named line.
/// <para>
/// Each use case logs under its own category, its command or query type
/// (<c>Trellis.Application.Features.Invoices.ListInvoices.ListInvoicesQuery</c>), so a namespace prefix in
/// <c>Logging:LogLevel</c> sets one feature's level. The logger is injected as <c>ILogger&lt;TCommand&gt;</c>,
/// not built from an <c>ILoggerFactory</c>, so a container that registers the open generic
/// <c>ILogger&lt;&gt;</c> (the unit-test harness's capturing logger) sees every line.
/// </para>
/// </summary>
public static partial class LoggingDecorators
{
    /// <summary>Caps the <c>field:code</c> list so a pathological payload cannot produce a pathological log line.</summary>
    private const int MaxLoggedValidationFailures = 10;

    /// <summary>Why each decorator's logger is typed by the message it wraps, not by the decorator (Sonar S6672).</summary>
    private const string CategoryPerUseCase =
        "Each use case logs under its own message type, so a namespace prefix in Logging:LogLevel sets one feature's level (ADR-0028).";

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S6672:Generic logger injection should match enclosing type", Justification = CategoryPerUseCase)]
    public sealed class CommandHandler<TCommand>(
        ICommandHandler<TCommand> inner,
        ILogger<TCommand> logger,
        UseCaseLoggingOptions options) : ICommandHandler<TCommand>
        where TCommand : ICommand
    {
        public Task<Result> Handle(TCommand command, CancellationToken cancellationToken)
            => Execute(logger, options, typeof(TCommand).Name, () => inner.Handle(command, cancellationToken), cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S6672:Generic logger injection should match enclosing type", Justification = CategoryPerUseCase)]
    public sealed class CommandHandler<TCommand, TResponse>(
        ICommandHandler<TCommand, TResponse> inner,
        ILogger<TCommand> logger,
        UseCaseLoggingOptions options) : ICommandHandler<TCommand, TResponse>
        where TCommand : ICommand<TResponse>
    {
        public Task<Result<TResponse>> Handle(TCommand command, CancellationToken cancellationToken)
            => Execute(logger, options, typeof(TCommand).Name, () => inner.Handle(command, cancellationToken), cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Minor Code Smell", "S6672:Generic logger injection should match enclosing type", Justification = CategoryPerUseCase)]
    public sealed class QueryHandler<TQuery, TResponse>(
        IQueryHandler<TQuery, TResponse> inner,
        ILogger<TQuery> logger,
        UseCaseLoggingOptions options) : IQueryHandler<TQuery, TResponse>
        where TQuery : IQuery<TResponse>
    {
        public Task<Result<TResponse>> Handle(TQuery query, CancellationToken cancellationToken)
            => Execute(logger, options, typeof(TQuery).Name, () => inner.Handle(query, cancellationToken), cancellationToken);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell", "S2139:Exceptions should be either logged or rethrown but not both",
        Justification = "This decorator IS the logging boundary: the log line carries the use-case name that is lost by the time the API's exception handler sees the throw.")]
    private static async Task<TResult> Execute<TResult>(
        ILogger logger, UseCaseLoggingOptions options, string useCase, Func<Task<TResult>> handle, CancellationToken cancellationToken)
        where TResult : Result
    {
        using IDisposable? scope = logger.BeginScope("UseCase {UseCase}", useCase);
        long started = Stopwatch.GetTimestamp();

        try
        {
            TResult result = await handle();
            LogOutcome(logger, options, useCase, result, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller gave up: an outcome worth one line, never an error, and its stack trace is noise.
            double elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            UseCaseCancelled(logger, useCase, elapsedMilliseconds);
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Keyed on the caller's token, not the exception type: a downstream timeout surfaces as an
            // OperationCanceledException too, and it is a failure the use case did not survive.
            double elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            UseCaseThrew(logger, exception, useCase, elapsedMilliseconds);
            throw;
        }
    }

    private static void LogOutcome(ILogger logger, UseCaseLoggingOptions options, string useCase, Result result, double elapsedMilliseconds)
    {
        if (result.IsSuccess)
        {
            UseCaseSucceeded(logger, useCase, elapsedMilliseconds);
            return;
        }

        Error error = result.Error;
        LogLevel level = IsClassifiedExpectedFailure(error.Type) ? LogLevel.Information : LogLevel.Warning;
        if (error is ValidationError validation)
        {
            if (logger.IsEnabled(level))
            {
                string failures = FailuresOf(validation);
                UseCaseFailedValidation(logger, level, useCase, elapsedMilliseconds, error.Type, error.Code, failures);
            }
        }
        else if (options.IncludeErrorDetail)
        {
            UseCaseFailedWithDetail(logger, level, useCase, elapsedMilliseconds, error.Type, error.Code, error.Description);
        }
        else
        {
            UseCaseFailed(logger, level, useCase, elapsedMilliseconds, error.Type, error.Code);
        }
    }

    /// <summary>
    /// The failures a caller causes and the API answers with a 4xx: outcomes, not alarms. That includes
    /// <see cref="ErrorType.PreconditionFailed"/>: a stale <c>If-Match</c> is a client that must reload, not
    /// a fault. <see cref="ErrorType.Unavailable"/> stays at Warning on purpose: nobody did anything wrong,
    /// but a dependency is down and someone should notice. <see cref="ErrorType.Failure"/>, and any type
    /// added later, logs at Warning too, until someone classifies it here.
    /// </summary>
    private static bool IsClassifiedExpectedFailure(ErrorType type)
        => type is ErrorType.Validation or ErrorType.NotFound or ErrorType.Conflict or ErrorType.Unauthorized
            or ErrorType.Forbidden or ErrorType.PreconditionFailed;

    /// <summary>What failed, never what was typed: <c>CustomerName:Validation.NotEmpty, …</c>; a whole-request entry by its code alone.</summary>
    private static string FailuresOf(ValidationError validation)
    {
        IEnumerable<string> failures = validation.Errors
            .Take(MaxLoggedValidationFailures)
            .Select(static e => e is FieldError field ? field.Field + ":" + field.Code : e.Code);

        string detail = string.Join(", ", failures);
        return validation.Errors.Count > MaxLoggedValidationFailures ? detail + ", …" : detail;
    }

    [LoggerMessage(EventId = 1001, EventName = "UseCaseSucceeded", Level = LogLevel.Information,
        Message = "Use case {UseCase} succeeded in {ElapsedMilliseconds}ms")]
    private static partial void UseCaseSucceeded(ILogger logger, string useCase, double elapsedMilliseconds);

    [LoggerMessage(EventId = 1002, EventName = "UseCaseFailed",
        Message = "Use case {UseCase} failed in {ElapsedMilliseconds}ms with {ErrorType} ({ErrorCode})")]
    private static partial void UseCaseFailed(
        ILogger logger, LogLevel level, string useCase, double elapsedMilliseconds, ErrorType errorType, string errorCode);

    [LoggerMessage(EventId = 1003, EventName = "UseCaseFailedValidation",
        Message = "Use case {UseCase} failed in {ElapsedMilliseconds}ms with {ErrorType} ({ErrorCode}): {ValidationFailures}")]
    private static partial void UseCaseFailedValidation(
        ILogger logger, LogLevel level, string useCase, double elapsedMilliseconds, ErrorType errorType, string errorCode, string validationFailures);

    /// <summary>Written instead of <see cref="UseCaseFailed"/> while <see cref="UseCaseLoggingOptions.IncludeErrorDetail"/> is on.</summary>
    [LoggerMessage(EventId = 1004, EventName = "UseCaseFailedWithDetail",
        Message = "Use case {UseCase} failed in {ElapsedMilliseconds}ms with {ErrorType} ({ErrorCode}): {ErrorDescription}")]
    private static partial void UseCaseFailedWithDetail(
        ILogger logger, LogLevel level, string useCase, double elapsedMilliseconds, ErrorType errorType, string errorCode, string errorDescription);

    [LoggerMessage(EventId = 1005, EventName = "UseCaseCancelled", Level = LogLevel.Information,
        Message = "Use case {UseCase} was cancelled by the caller after {ElapsedMilliseconds}ms")]
    private static partial void UseCaseCancelled(ILogger logger, string useCase, double elapsedMilliseconds);

    [LoggerMessage(EventId = 1006, EventName = "UseCaseThrew", Level = LogLevel.Error,
        Message = "Use case {UseCase} threw after {ElapsedMilliseconds}ms")]
    private static partial void UseCaseThrew(ILogger logger, Exception exception, string useCase, double elapsedMilliseconds);
}
