using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.SharedKernel.Domain;
using Xunit;

namespace Trellis.Application.UnitTests;

/// <summary>
/// The dispatcher's per-handler bookkeeping is what the outbox's retry semantics stand on: a handler
/// that already succeeded must never run again for the same message — across a retry in a fresh
/// process, and across a rename of its class.
/// </summary>
public sealed class DomainEventDispatcherTests
{
    private const string RecordingName = "test.recording";
    private const string FlakyName = "test.flaky";

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
        var dispatcher = new DomainEventDispatcher(provider);
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
        var dispatcher = new DomainEventDispatcher(provider);
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
            var dispatcher = new DomainEventDispatcher(firstProcess);
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
            var dispatcher = new DomainEventDispatcher(secondProcess);
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

        public Action? OnHandle { get; init; }

        public Task Handle(TestEvent domainEvent, CancellationToken cancellationToken)
        {
            Calls++;
            OnHandle?.Invoke();
            return Throw ? Task.FromException(new InvalidOperationException("downstream down")) : Task.CompletedTask;
        }
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
