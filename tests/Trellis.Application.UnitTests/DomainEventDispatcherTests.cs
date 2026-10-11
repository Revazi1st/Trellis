using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.SharedKernel.Domain;
using Xunit;

namespace Trellis.Application.UnitTests;

/// <summary>
/// The dispatcher's per-handler bookkeeping and isolation are what the outbox's retry semantics stand
/// on: a handler that already succeeded must never run again for the same message — across a retry in
/// a fresh process, and across a rename of its class — and a handler that fails must not stop its
/// siblings from running, only itself from being credited.
/// </summary>
public sealed class DomainEventDispatcherTests
{
    private const string RecordingName = "test.recording";
    private const string FlakyName = "test.flaky";
    private const string SecondFlakyName = "test.flaky-too";

    [Fact]
    public async Task Retry_after_a_partial_failure_skips_handlers_that_already_succeeded()
    {
        var succeeded = new RecordingHandler();
        var flaky = new FlakyHandler();
        await using ServiceProvider provider = Handlers(succeeded, flaky);
        IDomainEventDispatcher dispatcher = new DomainEventDispatcher(provider);

        var completed = new HashSet<string>(StringComparer.Ordinal);
        var domainEvent = new TestEvent();

        // First attempt: the recording handler succeeds, the flaky one throws.
        await Should.ThrowAsync<InvalidOperationException>(
            () => dispatcher.Dispatch(domainEvent, completed, CancellationToken.None));
        succeeded.Calls.ShouldBe(1);
        completed.ShouldBe([RecordingName]);

        // Retry (as the outbox would, with the persisted set): only the flaky handler runs again.
        flaky.Throw = false;
        await dispatcher.Dispatch(domainEvent, completed, CancellationToken.None);

        succeeded.Calls.ShouldBe(1);
        flaky.Calls.ShouldBe(2);
        completed.ShouldBe([RecordingName, FlakyName], ignoreOrder: true);
    }

    [Fact]
    public async Task Checkpoint_runs_after_each_successful_handler_and_before_it_is_credited()
    {
        List<string> sequence = [];
        await using ServiceProvider provider = Handlers(
            new RecordingHandler(() => sequence.Add("handler:" + RecordingName)),
            new FlakyHandler { Throw = false, OnHandle = () => sequence.Add("handler:" + FlakyName) });
        IDomainEventDispatcher dispatcher = new DomainEventDispatcher(provider);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        List<int> creditedWhenCheckpointed = [];

        await dispatcher.Dispatch(
            new TestEvent(),
            completed,
            name =>
            {
                sequence.Add("checkpoint:" + name);
                creditedWhenCheckpointed.Add(completed.Count);
                return Task.CompletedTask;
            },
            CancellationToken.None);

        sequence.ShouldBe([
            "handler:" + RecordingName, "checkpoint:" + RecordingName,
            "handler:" + FlakyName, "checkpoint:" + FlakyName,
        ]);
        creditedWhenCheckpointed.ShouldBe([0, 1]);   // the handler is credited only AFTER its checkpoint
        completed.ShouldBe([RecordingName, FlakyName], ignoreOrder: true);
    }

    [Fact]
    public async Task A_throwing_checkpoint_leaves_the_handler_uncredited()
    {
        var first = new RecordingHandler();
        var second = new FlakyHandler { Throw = false };
        await using ServiceProvider provider = Handlers(first, second);
        IDomainEventDispatcher dispatcher = new DomainEventDispatcher(provider);
        var completed = new HashSet<string>(StringComparer.Ordinal);

        // The second handler ran, but its writes could not be committed: it must run again on retry.
        await Should.ThrowAsync<InvalidOperationException>(() => dispatcher.Dispatch(
            new TestEvent(),
            completed,
            name => name == FlakyName ? Task.FromException(new InvalidOperationException("commit failed")) : Task.CompletedTask,
            CancellationToken.None));

        completed.ShouldBe([RecordingName]);
        second.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Checkpointed_credits_survive_a_retry_in_a_fresh_process()
    {
        // What the outbox persists: the completed set, written at every checkpoint.
        string? persisted = null;
        var completedFirst = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> current = completedFirst;
        Task Persist(string name)
        {
            persisted = JsonSerializer.Serialize(current.Append(name).Order(StringComparer.Ordinal));
            return Task.CompletedTask;
        }

        var succeeded = new RecordingHandler();
        await using (ServiceProvider firstProcess = Handlers(succeeded, new FlakyHandler()))
        {
            IDomainEventDispatcher dispatcher = new DomainEventDispatcher(firstProcess);
            await Should.ThrowAsync<InvalidOperationException>(
                () => dispatcher.Dispatch(new TestEvent(), completedFirst, Persist, CancellationToken.None));
        }

        // A new process: new handler instances, the credits read back from storage — nothing in memory.
        var succeededAgain = new RecordingHandler();
        var recovered = new FlakyHandler { Throw = false };
        HashSet<string> completedRetry = JsonSerializer.Deserialize<HashSet<string>>(persisted.ShouldNotBeNull())!;
        completedRetry.ShouldBe([RecordingName]);
        current = completedRetry;
        await using (ServiceProvider secondProcess = Handlers(succeededAgain, recovered))
        {
            IDomainEventDispatcher dispatcher = new DomainEventDispatcher(secondProcess);
            await dispatcher.Dispatch(new TestEvent(), completedRetry, Persist, CancellationToken.None);
        }

        succeeded.Calls.ShouldBe(1);
        succeededAgain.Calls.ShouldBe(0);
        recovered.Calls.ShouldBe(1);
        completedRetry.ShouldBe([RecordingName, FlakyName], ignoreOrder: true);
    }

    [Fact]
    public async Task Completion_is_keyed_on_the_stable_name_not_the_CLR_type_name()
    {
        var handler = new RecordingHandler();
        await using ServiceProvider provider = Handlers(handler);
        IDomainEventDispatcher dispatcher = new DomainEventDispatcher(provider);

        // A credit recorded under the old CLR-name scheme does not count: only the stable name does…
        await dispatcher.Dispatch(new TestEvent(), new HashSet<string>(StringComparer.Ordinal) { typeof(RecordingHandler).FullName! }, CancellationToken.None);
        handler.Calls.ShouldBe(1);

        // …so renaming or moving the class can never re-run a handler credited under its stable name.
        await dispatcher.Dispatch(new TestEvent(), new HashSet<string>(StringComparer.Ordinal) { RecordingName }, CancellationToken.None);
        handler.Calls.ShouldBe(1);
        DomainEventDispatcher.HandlerNameOf(typeof(RecordingHandler)).ShouldBe(RecordingName);
    }

    [Fact]
    public async Task A_handler_without_a_stable_name_fails_the_dispatch_before_any_handler_runs()
    {
        var named = new RecordingHandler();
        await using ServiceProvider provider = Handlers(named, new UnnamedHandler());
        IDomainEventDispatcher dispatcher = new DomainEventDispatcher(provider);

        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(
            () => dispatcher.Dispatch(new TestEvent(), new HashSet<string>(StringComparer.Ordinal), CancellationToken.None));

        error.Message.ShouldContain(nameof(UnnamedHandler));
        error.Message.ShouldContain("DomainEventHandlerName");
        named.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Two_handlers_sharing_a_name_fail_the_dispatch_instead_of_skipping_one()
    {
        var first = new RecordingHandler();
        await using ServiceProvider provider = Handlers(first, new ImpostorHandler());
        IDomainEventDispatcher dispatcher = new DomainEventDispatcher(provider);

        InvalidOperationException error = await Should.ThrowAsync<InvalidOperationException>(
            () => dispatcher.Dispatch(new TestEvent(), new HashSet<string>(StringComparer.Ordinal), CancellationToken.None));

        error.Message.ShouldContain(RecordingName);
        first.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task A_failing_handler_does_not_stop_its_siblings_and_is_rethrown_as_it_was()
    {
        var flaky = new FlakyHandler();        // first: throws
        var sibling = new RecordingHandler();  // second: must still get its turn
        await using ServiceProvider provider = Handlers(flaky, sibling);
        var dispatcher = new DomainEventDispatcher(provider);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        List<(string Handler, Exception Exception)> reported = [];

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(() => dispatcher.Dispatch(
            new TestEvent(),
            completed,
            checkpoint: null,
            onHandlerFailed: (handler, exception) =>
            {
                reported.Add((handler, exception));
                return Task.CompletedTask;
            },
            CancellationToken.None));

        thrown.Message.ShouldBe("downstream down");   // the single failure, unwrapped
        sibling.Calls.ShouldBe(1);
        completed.ShouldBe([RecordingName]);          // the sibling is credited; the failed handler alone is owed
        reported.ShouldHaveSingleItem();
        reported[0].Handler.ShouldBe(FlakyName);
        reported[0].Exception.ShouldBeSameAs(thrown);
    }

    [Fact]
    public async Task The_failure_hook_runs_after_the_failed_handler_and_before_the_next_one()
    {
        List<string> sequence = [];
        await using ServiceProvider provider = Handlers(
            new FlakyHandler { OnHandle = () => sequence.Add("handler:" + FlakyName) },
            new RecordingHandler(() => sequence.Add("handler:" + RecordingName)));
        var dispatcher = new DomainEventDispatcher(provider);

        await Should.ThrowAsync<InvalidOperationException>(() => dispatcher.Dispatch(
            new TestEvent(),
            new HashSet<string>(StringComparer.Ordinal),
            checkpoint: name =>
            {
                sequence.Add("checkpoint:" + name);
                return Task.CompletedTask;
            },
            onHandlerFailed: (name, _) =>
            {
                sequence.Add("failed:" + name);
                return Task.CompletedTask;
            },
            CancellationToken.None));

        sequence.ShouldBe(["handler:" + FlakyName, "failed:" + FlakyName, "handler:" + RecordingName, "checkpoint:" + RecordingName]);
    }

    [Fact]
    public async Task Several_failing_handlers_are_thrown_together_naming_each_one()
    {
        var survivor = new RecordingHandler();
        await using ServiceProvider provider = Handlers(new FlakyHandler(), survivor, new SecondFlakyHandler());
        IDomainEventDispatcher dispatcher = new DomainEventDispatcher(provider);
        var completed = new HashSet<string>(StringComparer.Ordinal);

        AggregateException thrown = await Should.ThrowAsync<AggregateException>(
            () => dispatcher.Dispatch(new TestEvent(), completed, CancellationToken.None));

        thrown.InnerExceptions.Count.ShouldBe(2);
        thrown.InnerExceptions[0].ShouldBeOfType<InvalidOperationException>();
        thrown.InnerExceptions[1].ShouldBeOfType<TimeoutException>();
        thrown.Message.ShouldContain("2 of the 3 handlers");
        thrown.Message.ShouldContain(FlakyName);
        thrown.Message.ShouldContain(SecondFlakyName);
        survivor.Calls.ShouldBe(1);
        completed.ShouldBe([RecordingName]);
    }

    [Fact]
    public async Task A_downstream_timeout_while_the_token_is_live_is_one_handlers_failure_like_any_other()
    {
        // What HttpClient throws on a timeout: an OperationCanceledException while nobody asked us to stop.
        var flaky = new FlakyHandler { Exception = static () => new TaskCanceledException("the receipt service timed out") };
        var sibling = new RecordingHandler();
        await using ServiceProvider provider = Handlers(flaky, sibling);
        IDomainEventDispatcher dispatcher = new DomainEventDispatcher(provider);
        var completed = new HashSet<string>(StringComparer.Ordinal);

        // Awaited as the outbox awaits it (Should.ThrowAsync would Wait() the cancelled task and see a fresh
        // "A task was canceled." instance): the original exception, message and all, is what comes out.
        Exception? thrown = await Record.ExceptionAsync(() => dispatcher.Dispatch(new TestEvent(), completed, CancellationToken.None));

        thrown.ShouldBeOfType<TaskCanceledException>().Message.ShouldBe("the receipt service timed out");
        sibling.Calls.ShouldBe(1);
        completed.ShouldBe([RecordingName]);
    }

    [Fact]
    public async Task A_cancelled_dispatch_token_ends_the_dispatch_at_once_and_is_not_isolated()
    {
        // The caller's budget (or the host) cancels while the first handler runs: nothing after it may start,
        // and the cancellation is not booked against the handler it happened to interrupt.
        using var budget = new CancellationTokenSource();
        var interrupted = new FlakyHandler { OnHandle = budget.Cancel, Exception = () => new OperationCanceledException(budget.Token) };
        var next = new RecordingHandler();
        await using ServiceProvider provider = Handlers(interrupted, next);
        var dispatcher = new DomainEventDispatcher(provider);
        var completed = new HashSet<string>(StringComparer.Ordinal);
        int reported = 0;

        await Should.ThrowAsync<OperationCanceledException>(() => dispatcher.Dispatch(
            new TestEvent(),
            completed,
            checkpoint: null,
            onHandlerFailed: (_, _) =>
            {
                reported++;
                return Task.CompletedTask;
            },
            budget.Token));

        next.Calls.ShouldBe(0);
        completed.ShouldBeEmpty();
        reported.ShouldBe(0);
    }

    [Fact]
    public async Task A_failing_checkpoint_on_the_first_handler_stops_the_dispatch_before_the_second_runs()
    {
        var first = new RecordingHandler();
        var second = new FlakyHandler { Throw = false };
        await using ServiceProvider provider = Handlers(first, second);
        IDomainEventDispatcher dispatcher = new DomainEventDispatcher(provider);
        var completed = new HashSet<string>(StringComparer.Ordinal);

        // A checkpoint failure is never isolated: nothing after it could be committed either.
        await Should.ThrowAsync<InvalidOperationException>(() => dispatcher.Dispatch(
            new TestEvent(),
            completed,
            name => name == RecordingName ? Task.FromException(new InvalidOperationException("commit failed")) : Task.CompletedTask,
            CancellationToken.None));

        first.Calls.ShouldBe(1);
        second.Calls.ShouldBe(0);
        completed.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_throwing_failure_hook_ends_the_dispatch()
    {
        var sibling = new RecordingHandler();
        await using ServiceProvider provider = Handlers(new FlakyHandler(), sibling);
        var dispatcher = new DomainEventDispatcher(provider);

        InvalidOperationException thrown = await Should.ThrowAsync<InvalidOperationException>(() => dispatcher.Dispatch(
            new TestEvent(),
            new HashSet<string>(StringComparer.Ordinal),
            checkpoint: null,
            onHandlerFailed: (_, _) => Task.FromException(new InvalidOperationException("the tracker could not be reset")),
            CancellationToken.None));

        thrown.Message.ShouldBe("the tracker could not be reset");
        sibling.Calls.ShouldBe(0);
    }

    private static ServiceProvider Handlers(params IDomainEventHandler<TestEvent>[] handlers)
    {
        var services = new ServiceCollection();
        foreach (IDomainEventHandler<TestEvent> handler in handlers)
        {
            services.AddSingleton(handler);
        }

        return services.BuildServiceProvider();
    }

    private sealed record TestEvent : IDomainEvent;

    [DomainEventHandlerName(RecordingName)]
    private sealed class RecordingHandler(Action? onHandle = null) : IDomainEventHandler<TestEvent>
    {
        public int Calls { get; private set; }

        public Task Handle(TestEvent domainEvent, CancellationToken cancellationToken)
        {
            Calls++;
            onHandle?.Invoke();
            return Task.CompletedTask;
        }
    }

    [DomainEventHandlerName(FlakyName)]
    private sealed class FlakyHandler : IDomainEventHandler<TestEvent>
    {
        public int Calls { get; private set; }

        public bool Throw { get; set; } = true;

        public Func<Exception> Exception { get; init; } = static () => new InvalidOperationException("downstream down");

        public Action? OnHandle { get; init; }

        public Task Handle(TestEvent domainEvent, CancellationToken cancellationToken)
        {
            Calls++;
            OnHandle?.Invoke();
            return Throw ? Task.FromException(Exception()) : Task.CompletedTask;
        }
    }

    [DomainEventHandlerName(SecondFlakyName)]
    private sealed class SecondFlakyHandler : IDomainEventHandler<TestEvent>
    {
        public Task Handle(TestEvent domainEvent, CancellationToken cancellationToken)
            => Task.FromException(new TimeoutException("also down"));
    }

    private sealed class UnnamedHandler : IDomainEventHandler<TestEvent>
    {
        public Task Handle(TestEvent domainEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [DomainEventHandlerName(RecordingName)]
    private sealed class ImpostorHandler : IDomainEventHandler<TestEvent>
    {
        public Task Handle(TestEvent domainEvent, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
