using System.Diagnostics;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using Trellis.Application.Features.Invoices.CreateInvoice;
using Trellis.Application.Features.Invoices.PayInvoice;
using Trellis.Domain.Invoices;
using Trellis.SharedKernel.Decorators;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Messaging;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.Application.UnitTests;

/// <summary>
/// The pipeline's outcome record: every use-case invocation writes exactly one line from the logging
/// stage, inside its one span, and the log level follows the outcome class — Information for success
/// and for classified expected failures, Warning for an unclassified failure, Error only for an
/// exception that ends the use case. Driven through the real container with a capturing logger.
/// </summary>
public sealed class UseCaseOutcomeTests : IDisposable
{
    private readonly PipelineHarness _pipeline = new(static services =>
    {
        services.AddScoped<ICommandHandler<ScriptedCommand>, ScriptedCommandHandler>();
        services.AddScoped<ICommandHandler<GuardedCommand>, GuardedCommandHandler>();
        services.AddSingleton<IValidator<GuardedCommand>>(new InlineValidator<GuardedCommand>
        {
            static rules => rules.RuleFor(static c => c.Name).NotEmpty(),
        });
    });

    public void Dispose() => _pipeline.Dispose();

    [Fact]
    public async Task Success_is_one_Information_line()
    {
        await Handle(new ScriptedCommand(static _ => Task.FromResult(Result.Success())));

        LogEntry line = _pipeline.Logs.OutcomeLines.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Information);
        line.EventId.Id.ShouldBe(1001);
        line.Message.ShouldContain("ScriptedCommand succeeded");
    }

    [Fact]
    public async Task Invalid_command_still_produces_one_Information_line_naming_each_field_and_rule()
    {
        var handler = _pipeline.Resolve<ICommandHandler<CreateInvoiceCommand, InvoiceId>>();

        await handler.Handle(new CreateInvoiceCommand("", "", "USD"), TestContext.Current.CancellationToken);

        // Validation sits INSIDE Logging: the rejected request is not invisible.
        LogEntry line = _pipeline.Logs.OutcomeLines.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Information);
        line.EventId.Id.ShouldBe(1003);
        line.Message.ShouldContain("Validation (Validation.General)");
        line.Message.ShouldContain("CustomerName:Validation.NotEmpty");
        line.Message.ShouldContain("CustomerEmail:Validation.NotEmpty");
    }

    [Fact]
    public async Task Not_found_is_logged_at_Information()
    {
        _pipeline.Invoices.GetByIdAsync(Arg.Any<InvoiceId>(), Arg.Any<CancellationToken>()).Returns((Invoice?)null);
        var handler = _pipeline.Resolve<ICommandHandler<PayInvoiceCommand>>();

        await handler.Handle(new PayInvoiceCommand(InvoiceId.New()), TestContext.Current.CancellationToken);

        LogEntry line = _pipeline.Logs.OutcomeLines.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Information);
        line.Message.ShouldContain("Invoice.NotFound");
    }

    [Fact]
    public async Task Concurrency_conflict_at_commit_is_logged_at_Information_not_as_an_exception()
    {
        _pipeline.UnitOfWork.CommitAsync(Arg.Any<CancellationToken>()).Returns(Result.Failure(UnitOfWorkErrors.ConcurrencyConflict));

        Result result = await Handle(new ScriptedCommand(static _ => Task.FromResult(Result.Success())));

        result.Error.ShouldBe(UnitOfWorkErrors.ConcurrencyConflict);
        LogEntry line = _pipeline.Logs.OutcomeLines.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Information);
        line.Message.ShouldContain(UnitOfWorkErrors.ConcurrencyConflict.Code);
        line.Exception.ShouldBeNull();
    }

    [Fact]
    public async Task Unclassified_failure_is_logged_at_Warning()
    {
        await Handle(new ScriptedCommand(static _ =>
            Task.FromResult(Result.Failure(Error.Failure("Test.Unclassified", "Nobody classified this yet.")))));

        LogEntry line = _pipeline.Logs.OutcomeLines.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Warning);
        line.EventId.Id.ShouldBe(1002);
        line.Message.ShouldContain("Failure (Test.Unclassified)");
        line.Message.ShouldNotContain("Nobody classified this yet.");
    }

    [Fact]
    public async Task A_failure_line_names_the_type_and_code_but_never_the_description()
    {
        // A description can carry what the user typed: Identity's duplicate-account text names the e-mail.
        await Handle(new ScriptedCommand(static _ =>
            Task.FromResult(Result.Failure(Error.Conflict("Test.DuplicateAccount", "Email 'jane@example.com' is already taken.")))));

        LogEntry line = _pipeline.Logs.OutcomeLines.ShouldHaveSingleItem();
        line.Message.ShouldContain("Conflict (Test.DuplicateAccount)");
        line.Message.ShouldNotContain("jane@example.com");
    }

    [Fact]
    public async Task With_IncludeErrorDetail_the_failure_line_carries_the_description()
    {
        // Development's opt-in: registered before AddApplication(), the host's instance wins over the default.
        using var pipeline = new PipelineHarness(static services =>
        {
            services.AddSingleton(new UseCaseLoggingOptions { IncludeErrorDetail = true });
            services.AddScoped<ICommandHandler<ScriptedCommand>, ScriptedCommandHandler>();
        });

        await pipeline.Resolve<ICommandHandler<ScriptedCommand>>().Handle(
            new ScriptedCommand(static _ => Task.FromResult(Result.Failure(Error.Failure("Test.Unclassified", "Nobody classified this yet.")))),
            TestContext.Current.CancellationToken);

        LogEntry line = pipeline.Logs.OutcomeLines.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Warning);
        line.EventId.Id.ShouldBe(1004);
        line.Message.ShouldContain("Failure (Test.Unclassified): Nobody classified this yet.");
    }

    [Theory]
    [InlineData(ErrorType.Validation, LogLevel.Information)]
    [InlineData(ErrorType.NotFound, LogLevel.Information)]
    [InlineData(ErrorType.Conflict, LogLevel.Information)]
    [InlineData(ErrorType.Unauthorized, LogLevel.Information)]
    [InlineData(ErrorType.Forbidden, LogLevel.Information)]
    [InlineData(ErrorType.Failure, LogLevel.Warning)]
    public async Task Every_error_type_logs_one_line_at_its_level(ErrorType type, LogLevel expected)
    {
        var error = new Error("Test." + type, "Scripted.", type);

        await Handle(new ScriptedCommand(_ => Task.FromResult(Result.Failure(error))));

        LogEntry line = _pipeline.Logs.OutcomeLines.ShouldHaveSingleItem();
        line.Level.ShouldBe(expected);
        line.Message.ShouldContain("Test." + type);
    }

    [Fact]
    public async Task Throwing_handler_is_logged_at_Error_and_rethrown()
    {
        var boom = new InvalidOperationException("boom");

        await Should.ThrowAsync<InvalidOperationException>(() => Handle(new ScriptedCommand(_ => throw boom)));

        LogEntry line = _pipeline.Logs.OutcomeLines.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Error);
        line.EventId.Id.ShouldBe(1006);
        line.Exception.ShouldBeSameAs(boom);
        await _pipeline.UnitOfWork.DidNotReceive().CommitAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_downstream_cancellation_is_an_Error_when_the_caller_did_not_cancel()
    {
        // An HttpClient timeout surfaces as TaskCanceledException while the request is very much alive:
        // a fault the use case did not survive, keyed on the caller's token, not the exception type.
        await Should.ThrowAsync<TaskCanceledException>(() =>
            Handle(new ScriptedCommand(static _ => throw new TaskCanceledException("downstream timed out"))));

        LogEntry line = _pipeline.Logs.OutcomeLines.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Error);
        line.Exception.ShouldBeOfType<TaskCanceledException>();
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_one_Information_line_never_an_Error()
    {
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();

        await Should.ThrowAsync<OperationCanceledException>(() => Handle(
            new ScriptedCommand(static ct =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(Result.Success());
            }),
            caller.Token));

        LogEntry line = _pipeline.Logs.OutcomeLines.ShouldHaveSingleItem();
        line.Level.ShouldBe(LogLevel.Information);
        line.EventId.Id.ShouldBe(1005);
        line.Message.ShouldContain("cancelled by the caller");
    }

    [Fact]
    public async Task A_throwing_handler_marks_the_span_as_an_error_and_records_the_exception()
    {
        using var spans = new SpanRecorder();

        await Should.ThrowAsync<InvalidOperationException>(() =>
            Handle(new ScriptedCommand(static _ => throw new InvalidOperationException("boom"))));

        Activity span = spans.Of(nameof(ScriptedCommand)).ShouldHaveSingleItem();
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.GetTagItem(TracingDecorators.OutcomeTag).ShouldBe("exception");
        ActivityEvent exceptionEvent = span.Events.ShouldHaveSingleItem();
        exceptionEvent.Name.ShouldBe("exception");
        exceptionEvent.Tags.ShouldContain(static t => t.Key == "exception.type" && Equals(t.Value, typeof(InvalidOperationException).FullName));
    }

    [Fact]
    public async Task A_downstream_cancellation_marks_the_span_as_an_error()
    {
        using var spans = new SpanRecorder();

        await Should.ThrowAsync<TaskCanceledException>(() =>
            Handle(new ScriptedCommand(static _ => throw new TaskCanceledException("downstream timed out"))));

        Activity span = spans.Of(nameof(ScriptedCommand)).ShouldHaveSingleItem();
        span.Status.ShouldBe(ActivityStatusCode.Error);
        span.Events.ShouldContain(static e => e.Name == "exception");
    }

    [Fact]
    public async Task An_expected_failure_tags_the_span_with_its_outcome_but_leaves_the_status_unset()
    {
        using var spans = new SpanRecorder();

        await Handle(new ScriptedCommand(static _ =>
            Task.FromResult(Result.Failure(Error.Conflict("Test.Conflict", "Already done.")))));

        Activity span = spans.Of(nameof(ScriptedCommand)).ShouldHaveSingleItem();
        span.Status.ShouldBe(ActivityStatusCode.Unset);
        span.GetTagItem(TracingDecorators.OutcomeTag).ShouldBe("failure");
        span.GetTagItem(TracingDecorators.ErrorCodeTag).ShouldBe("Test.Conflict");
        span.GetTagItem(TracingDecorators.ErrorTypeTag).ShouldBe(nameof(ErrorType.Conflict));
        span.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_outcome_line_is_written_inside_the_use_case_span()
    {
        using var spans = new SpanRecorder();

        await Handle(new ScriptedCommand(static _ => Task.FromResult(Result.Success())));

        // Tracing wraps Logging: the line is written while the span is current, so a backend links the
        // outcome line to the use case's span rather than to the HTTP request around it.
        Activity span = spans.Of(nameof(ScriptedCommand)).ShouldHaveSingleItem();
        LogEntry line = _pipeline.Logs.OutcomeLines.ShouldHaveSingleItem();
        line.SpanId.ShouldBe(span.SpanId);
    }

    [Fact]
    public async Task A_rejected_request_still_gets_its_span()
    {
        using var spans = new SpanRecorder();
        var handler = _pipeline.Resolve<ICommandHandler<GuardedCommand>>();

        Result result = await handler.Handle(new GuardedCommand(""), TestContext.Current.CancellationToken);

        result.Error.ShouldBeOfType<ValidationError>();
        Activity span = spans.Of(nameof(GuardedCommand)).ShouldHaveSingleItem();
        span.GetTagItem(TracingDecorators.OutcomeTag).ShouldBe("failure");
        span.GetTagItem(TracingDecorators.ErrorCodeTag).ShouldBe("Validation.General");
    }

    private Task<Result> Handle(ScriptedCommand command, CancellationToken? cancellationToken = null)
        => _pipeline.Resolve<ICommandHandler<ScriptedCommand>>()
            .Handle(command, cancellationToken ?? TestContext.Current.CancellationToken);

    /// <summary>A test-only use case whose handler does whatever the test scripts — through the real pipeline.</summary>
    private sealed record ScriptedCommand(Func<CancellationToken, Task<Result>> Behavior) : ICommand;

    private sealed class ScriptedCommandHandler : ICommandHandler<ScriptedCommand>
    {
        public Task<Result> Handle(ScriptedCommand command, CancellationToken cancellationToken)
            => command.Behavior(cancellationToken);
    }

    /// <summary>A test-only use case with a validator; its type name is unique, so its spans are unambiguous.</summary>
    private sealed record GuardedCommand(string Name) : ICommand;

    private sealed class GuardedCommandHandler : ICommandHandler<GuardedCommand>
    {
        public Task<Result> Handle(GuardedCommand command, CancellationToken cancellationToken) => Task.FromResult(Result.Success());
    }

    /// <summary>
    /// Records the use-case spans that stop while it is alive. The source is process-wide, so callers
    /// filter by operation name (the use case's type name).
    /// </summary>
    private sealed class SpanRecorder : IDisposable
    {
        private readonly List<Activity> _stopped = [];
        private readonly ActivityListener _listener;

        public SpanRecorder()
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = static source => source.Name == TracingDecorators.ActivitySourceName,
                Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    lock (_stopped)
                    {
                        _stopped.Add(activity);
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public IReadOnlyList<Activity> Of(string operationName)
        {
            lock (_stopped)
            {
                return [.. _stopped.Where(a => a.OperationName == operationName)];
            }
        }

        public void Dispose() => _listener.Dispose();
    }
}
