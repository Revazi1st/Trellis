using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.SharedKernel.Domain;
using Xunit;

namespace Trellis.Application.UnitTests;

/// <summary>
/// The dispatcher's per-handler bookkeeping is what the outbox's retry semantics stand on:
/// a handler that already succeeded must never run again for the same message.
/// </summary>
public sealed class DomainEventDispatcherTests
{
    private sealed record TestEvent : IDomainEvent;

    private sealed class RecordingHandler : IDomainEventHandler<TestEvent>
    {
        public int Calls { get; private set; }

        public Task Handle(TestEvent domainEvent, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    private sealed class FlakyHandler : IDomainEventHandler<TestEvent>
    {
        public int Calls { get; private set; }

        public bool Throw { get; set; } = true;

        public Task Handle(TestEvent domainEvent, CancellationToken cancellationToken)
        {
            Calls++;
            return Throw ? Task.FromException(new InvalidOperationException("downstream down")) : Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Retry_after_a_partial_failure_skips_handlers_that_already_succeeded()
    {
        var succeeded = new RecordingHandler();
        var flaky = new FlakyHandler();
        var services = new ServiceCollection();
        services.AddSingleton<IDomainEventHandler<TestEvent>>(succeeded);
        services.AddSingleton<IDomainEventHandler<TestEvent>>(flaky);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var dispatcher = new DomainEventDispatcher(provider);

        var completed = new HashSet<string>(StringComparer.Ordinal);
        var domainEvent = new TestEvent();

        // First attempt: the recording handler succeeds, the flaky one throws.
        await Should.ThrowAsync<InvalidOperationException>(
            () => dispatcher.Dispatch(domainEvent, completed, CancellationToken.None));
        succeeded.Calls.ShouldBe(1);
        completed.ShouldContain(typeof(RecordingHandler).FullName!, StringComparer.Ordinal);
        completed.ShouldNotContain(typeof(FlakyHandler).FullName!, StringComparer.Ordinal);

        // Retry (as the outbox would, with the persisted set): only the flaky handler runs again.
        flaky.Throw = false;
        await dispatcher.Dispatch(domainEvent, completed, CancellationToken.None);

        succeeded.Calls.ShouldBe(1);
        flaky.Calls.ShouldBe(2);
        completed.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Collection_dispatch_invokes_every_handler()
    {
        var first = new RecordingHandler();
        var services = new ServiceCollection();
        services.AddSingleton<IDomainEventHandler<TestEvent>>(first);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var dispatcher = new DomainEventDispatcher(provider);

        await dispatcher.Dispatch([new TestEvent(), new TestEvent()], CancellationToken.None);

        first.Calls.ShouldBe(2);
    }
}
