using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Trellis.Application.Features.Invoices.AddInvoiceLine;
using Trellis.Application.Features.Invoices.CreateInvoice;
using Trellis.Application.Features.Invoices.IssueInvoice;
using Trellis.Application.Features.Invoices.PayInvoice;
using Trellis.Domain.Common;
using Trellis.Domain.Invoices;
using Trellis.Domain.Invoices.Events;
using Trellis.IntegrationTests.Telemetry;
using Trellis.Persistence;
using Trellis.Persistence.Outbox;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Messaging;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// The processor's failure semantics against the real database (ADR-0023): what a row looks like after a
/// handler throws, times out, writes, or cannot even be read; what a second processor and a stopping host
/// see; and what the meters and spans report. Three test handlers of <c>InvoicePaidDomainEvent</c> are
/// registered after the real ones (so they are "the later handlers") and switched on per test; the real
/// receipt e-mail is the side effect that must never be sent twice.
/// </summary>
public sealed class OutboxRetryTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    private const string PaidType = "invoices.paid.v1";
    private const string IssuedType = "invoices.issued.v1";
    private const string SendReceipt = "invoices.paid.send-receipt";
    private const string RecordMetrics = "invoices.paid.record-metrics";
    private const string FlakyName = "test.flaky";
    private const string WritingName = "test.writing";

    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(30);

    private readonly WritingSwitch _writing = new();
    private readonly FlakyPaidHandler _flaky = new();
    private readonly BlockingPaidHandler _blocking = new();
    private readonly BookkeepingFault _bookkeepingFault = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected override void ConfigureServices(IServiceCollection services)
    {
        // In this order after the scanned handlers: a writer (scoped, so it shares the message scope's
        // repository), then one that can throw, then one that can block. Each is a no-op until a test flips it.
        services.AddSingleton(_writing);
        services.AddScoped<IDomainEventHandler<InvoicePaidDomainEvent>, WritingPaidHandler>();
        services.AddSingleton<IDomainEventHandler<InvoicePaidDomainEvent>>(_flaky);
        services.AddSingleton<IDomainEventHandler<InvoicePaidDomainEvent>>(_blocking);

        // Appended after the app's own interceptors; inert until a test names the row whose saves fail.
        services.ConfigureDbContext<AppDbContext>(options => options.AddInterceptors(_bookkeepingFault));
    }

    [Fact]
    public async Task A_throwing_second_handler_schedules_a_retry_and_keeps_the_handlers_that_succeeded()
    {
        _flaky.Throw = true;
        InvoiceId id = await PayNewInvoiceAsync();

        (await CreateOutboxProcessor().ProcessBatchAsync(Ct)).ShouldBe(2);

        OutboxMessage paid = await RowAsync(PaidType);
        paid.Status.ShouldBe(OutboxMessageStatus.Pending);
        paid.Attempts.ShouldBe(1);
        paid.NextAttemptAtUtc.ShouldBe(Now + TimeSpan.FromSeconds(30));
        paid.Error.ShouldBe("downstream down");
        paid.ProcessedAtUtc.ShouldBeNull();
        ShouldCredit(paid, SendReceipt, RecordMetrics);
        ShouldNotCredit(paid, FlakyName);
        _flaky.Calls.ShouldBe(1);
        await EmailSender.Received(1).SendAsync("billing@acme.com", Arg.Any<string>(), Arg.Is<string>(body => body.Contains(id.ToString())), Arg.Any<CancellationToken>());

        // Isolation across the batch: the other message of the same claim is finished, not dragged along.
        (await RowAsync(IssuedType)).Status.ShouldBe(OutboxMessageStatus.Processed);
    }

    [Fact]
    public async Task A_message_in_backoff_is_not_claimed_before_its_next_attempt()
    {
        _flaky.Throw = true;
        await PayNewInvoiceAsync();
        OutboxProcessor processor = CreateOutboxProcessor();
        await processor.ProcessBatchAsync(Ct);
        OutboxMessage before = await RowAsync(PaidType);

        (await processor.ProcessBatchAsync(Ct)).ShouldBe(0);

        OutboxMessage after = await RowAsync(PaidType);
        after.Status.ShouldBe(OutboxMessageStatus.Pending);
        after.Attempts.ShouldBe(before.Attempts);
        after.NextAttemptAtUtc.ShouldBe(before.NextAttemptAtUtc);
        after.Error.ShouldBe(before.Error);
        _flaky.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task After_the_backoff_elapses_only_the_failed_handler_runs_again()
    {
        _flaky.Throw = true;
        await PayNewInvoiceAsync();
        OutboxProcessor processor = CreateOutboxProcessor();
        await processor.ProcessBatchAsync(Ct);

        Time.Advance(TimeSpan.FromSeconds(30));
        _flaky.Throw = false;
        (await processor.ProcessBatchAsync(Ct)).ShouldBe(1);

        OutboxMessage paid = await RowAsync(PaidType);
        paid.Status.ShouldBe(OutboxMessageStatus.Processed);
        paid.Error.ShouldBeNull();
        paid.ProcessedAtUtc.ShouldBe(Time.GetUtcNow());
        paid.Attempts.ShouldBe(1);   // the successful attempt is not a failure to count
        ShouldCredit(paid, SendReceipt, FlakyName);
        _flaky.Calls.ShouldBe(2);
        await EmailSender.Received(1).SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Exhausting_the_attempts_dead_letters_the_message_and_counts_it()
    {
        using var recorder = new MetricRecorder(MeterFactory);
        var options = new OutboxOptions { MaxAttempts = 3 };
        _flaky.Throw = true;
        await PayNewInvoiceAsync();
        OutboxProcessor processor = CreateOutboxProcessor(options);

        for (int attempt = 1; attempt <= options.MaxAttempts; attempt++)
        {
            await processor.ProcessBatchAsync(Ct);
            Time.Advance(OutboxBackoff.DelayAfter(options, attempt));
        }

        OutboxMessage paid = await RowAsync(PaidType);
        paid.Status.ShouldBe(OutboxMessageStatus.DeadLettered);
        paid.Attempts.ShouldBe(3);
        paid.Error.ShouldBe("downstream down");
        paid.ProcessedAtUtc.ShouldBeNull();
        ShouldCredit(paid, SendReceipt);   // kept, so a re-drive does not send the receipt again
        _flaky.Calls.ShouldBe(3);
        await EmailSender.Received(1).SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        recorder.Total("trellis.outbox.dead_lettered", Tag(OutboxMetrics.EventTypeAttribute, PaidType), Tag(OutboxMetrics.ReasonAttribute, "exhausted")).ShouldBe(1);
        recorder.Total("trellis.outbox.retries", Tag(OutboxMetrics.EventTypeAttribute, PaidType), Tag(OutboxMetrics.ErrorTypeAttribute, "InvalidOperationException")).ShouldBe(2);
        recorder.Total("trellis.outbox.dispatched").ShouldBe(1);   // the issued row
        recorder.Of("trellis.outbox.dispatch.duration").Count(m => Equals(m.Tags[OutboxMetrics.OutcomeAttribute], "retry_scheduled")).ShouldBe(2);
        recorder.Of("trellis.outbox.dispatch.duration").Count(m => Equals(m.Tags[OutboxMetrics.OutcomeAttribute], "dead_lettered")).ShouldBe(1);

        // A dead letter is not pending: nothing claims it again.
        (await processor.ProcessBatchAsync(Ct)).ShouldBe(0);
    }

    [Fact]
    public async Task An_unknown_event_type_is_dead_lettered_without_retrying()
    {
        using var recorder = new MetricRecorder(MeterFactory);
        OutboxMessage unknown = Row("invoices.unknown.v9", "{}");
        await InsertAsync(unknown);

        (await CreateOutboxProcessor().ProcessBatchAsync(Ct)).ShouldBe(1);

        OutboxMessage stored = await RowAsync(unknown.Id);
        stored.Status.ShouldBe(OutboxMessageStatus.DeadLettered);
        stored.Attempts.ShouldBe(0);
        stored.Error.ShouldNotBeNull().ShouldStartWith("Unknown event type 'invoices.unknown.v9'");
        recorder.Total("trellis.outbox.dead_lettered", Tag(OutboxMetrics.ReasonAttribute, "unknown_type")).ShouldBe(1);
        recorder.Total("trellis.outbox.retries").ShouldBe(0);
        await EmailSender.DidNotReceiveWithAnyArgs().SendAsync(default!, default!, default!, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_malformed_payload_is_dead_lettered_without_touching_the_rest_of_the_batch()
    {
        using var recorder = new MetricRecorder(MeterFactory);
        // Older than the real rows, so it is first in the batch: the rows after it must still be dispatched.
        OutboxMessage poison = Row(PaidType, """{"invoiceId":"not-a-guid","customerEmail":"x@y.dev"}""", occurredAt: Now - TimeSpan.FromMinutes(1));
        await InsertAsync(poison);
        InvoiceId id = await PayNewInvoiceAsync();

        (await CreateOutboxProcessor().ProcessBatchAsync(Ct)).ShouldBe(3);

        OutboxMessage stored = await RowAsync(poison.Id);
        stored.Status.ShouldBe(OutboxMessageStatus.DeadLettered);
        stored.Attempts.ShouldBe(0);
        stored.Error.ShouldNotBeNull().ShouldStartWith("Payload does not deserialize");
        recorder.Total("trellis.outbox.dead_lettered", Tag(OutboxMetrics.ReasonAttribute, "poison")).ShouldBe(1);

        (await RowAsync(PaidType, except: poison.Id)).Status.ShouldBe(OutboxMessageStatus.Processed);
        (await RowAsync(IssuedType)).Status.ShouldBe(OutboxMessageStatus.Processed);
        await EmailSender.Received(1).SendAsync("billing@acme.com", Arg.Any<string>(), Arg.Is<string>(body => body.Contains(id.ToString())), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Two_concurrent_processors_dispatch_each_message_exactly_once()
    {
        using var recorder = new MetricRecorder(MeterFactory);
        for (int i = 0; i < 6; i++)
        {
            await PayNewInvoiceAsync();   // twelve rows: six issued, six paid
        }

        var options = new OutboxOptions { BatchSize = 4 };
        var metrics = new OutboxMetrics(MeterFactory);
        OutboxProcessor first = CreateOutboxProcessor(options, metrics);
        OutboxProcessor second = CreateOutboxProcessor(options, metrics);

        int rounds = 0;
        int claimed;
        do
        {
            int[] perProcessor = await Task.WhenAll(first.ProcessBatchAsync(Ct), second.ProcessBatchAsync(Ct));
            claimed = perProcessor.Sum();
            rounds++;
        }
        while (claimed > 0 && rounds < 10);

        List<OutboxMessage> rows = await RowsAsync();
        rows.Count.ShouldBe(12);
        rows.ShouldAllBe(static r => r.Status == OutboxMessageStatus.Processed && r.Attempts == 0);
        await EmailSender.Received(6).SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        recorder.Total("trellis.outbox.dispatched").ShouldBe(12);
    }

    [Fact]
    public async Task A_downstream_cancellation_is_a_retryable_failure_not_a_shutdown()
    {
        // What HttpClient throws on a timeout: an OperationCanceledException while nobody asked us to stop.
        _flaky.Throw = true;
        _flaky.Exception = static () => new TaskCanceledException("the receipt service timed out");
        await PayNewInvoiceAsync();

        Task<int> run = CreateOutboxProcessor().ProcessBatchAsync(Ct);
        await Should.NotThrowAsync(() => run);

        (await run).ShouldBe(2);
        OutboxMessage paid = await RowAsync(PaidType);
        paid.Status.ShouldBe(OutboxMessageStatus.Pending);
        paid.Attempts.ShouldBe(1);
        paid.Error.ShouldBe("the receipt service timed out");
        paid.NextAttemptAtUtc.ShouldBe(Now + TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_handler_that_exceeds_the_dispatch_timeout_is_retried_without_stopping_the_host()
    {
        _blocking.Block = true;
        await PayNewInvoiceAsync();
        var options = new OutboxOptions { DispatchTimeout = TimeSpan.FromSeconds(5) };

        Task<int> run = CreateOutboxProcessor(options).ProcessBatchAsync(Ct);
        await _blocking.Started.Task.WaitAsync(WaitBudget, Ct);
        Time.Advance(options.DispatchTimeout);   // the budget fires; only the injected clock can end the wait
        (await run.WaitAsync(WaitBudget, Ct)).ShouldBe(2);

        OutboxMessage paid = await RowAsync(PaidType);
        paid.Status.ShouldBe(OutboxMessageStatus.Pending);
        paid.Attempts.ShouldBe(1);
        paid.Error.ShouldNotBeNull().ShouldContain("canceled");
        // The back-off replaced the lease (which would have run until DispatchTimeout × 21), so a retry is near.
        paid.NextAttemptAtUtc.ShouldBe(Time.GetUtcNow() + TimeSpan.FromSeconds(30));
        ShouldCredit(paid, SendReceipt);

        // The retry, with the handler no longer stuck, finishes the message without a second receipt.
        _blocking.Block = false;
        Time.Advance(TimeSpan.FromSeconds(30));
        (await CreateOutboxProcessor(options).ProcessBatchAsync(Ct)).ShouldBe(1);
        (await RowAsync(PaidType)).Status.ShouldBe(OutboxMessageStatus.Processed);
        await EmailSender.Received(1).SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_writing_handler_is_committed_with_its_credit_even_when_a_later_handler_fails()
    {
        _writing.Enabled = true;
        _flaky.Throw = true;
        await PayNewInvoiceAsync();
        OutboxProcessor processor = CreateOutboxProcessor();

        await processor.ProcessBatchAsync(Ct);

        // The handler's invoice is durable although the message failed after it: committed at its checkpoint.
        (await InvoiceCountAsync()).ShouldBe(2);
        OutboxMessage paid = await RowAsync(PaidType);
        paid.Status.ShouldBe(OutboxMessageStatus.Pending);
        ShouldCredit(paid, WritingName);
        ShouldNotCredit(paid, FlakyName);

        Time.Advance(TimeSpan.FromSeconds(30));
        _flaky.Throw = false;
        await processor.ProcessBatchAsync(Ct);

        (await RowAsync(PaidType)).Status.ShouldBe(OutboxMessageStatus.Processed);
        (await InvoiceCountAsync()).ShouldBe(2);   // credited means skipped: no second invoice
    }

    [Fact]
    public async Task Failed_dispatch_marks_the_span_as_error_with_message_tags()
    {
        using var activities = new ActivityRecorder();
        _flaky.Throw = true;
        await PayNewInvoiceAsync();
        OutboxMessage unknown = Row("invoices.unknown.v9", "{}");
        await InsertAsync(unknown);

        (await CreateOutboxProcessor().ProcessBatchAsync(Ct)).ShouldBe(3);

        OutboxMessage paid = await RowAsync(PaidType);
        Activity failed = activities.Stopped.Single(a => Equals(a.GetTagItem(OutboxMetrics.MessageIdAttribute), paid.Id));
        failed.OperationName.ShouldBe("outbox invoices.paid.v1");
        failed.Status.ShouldBe(ActivityStatusCode.Error);
        failed.StatusDescription.ShouldBe("downstream down");
        failed.Events.Count(static e => e.Name == "exception").ShouldBe(1);
        failed.GetTagItem(OutboxMetrics.EventTypeAttribute).ShouldBe(PaidType);
        failed.GetTagItem(OutboxMetrics.AttemptAttribute).ShouldBe(1);
        failed.GetTagItem(OutboxMetrics.OutcomeAttribute).ShouldBe("retry_scheduled");

        // The unreadable row got a span too — it was started before deserialization.
        Activity dead = activities.Stopped.Single(a => Equals(a.GetTagItem(OutboxMetrics.MessageIdAttribute), unknown.Id));
        dead.Status.ShouldBe(ActivityStatusCode.Error);
        dead.GetTagItem(OutboxMetrics.OutcomeAttribute).ShouldBe("dead_lettered");
        dead.GetTagItem(OutboxMetrics.ReasonAttribute).ShouldBe("unknown_type");

        Activity processed = activities.Stopped.Single(static a => Equals(a.GetTagItem(OutboxMetrics.EventTypeAttribute), IssuedType));
        processed.Status.ShouldBe(ActivityStatusCode.Unset);
        processed.GetTagItem(OutboxMetrics.OutcomeAttribute).ShouldBe("processed");
    }

    [Fact]
    public async Task A_full_batch_reports_its_size_so_the_loop_drains_without_sleeping()
    {
        for (int i = 0; i < 3; i++)
        {
            await PayNewInvoiceAsync();   // six rows
        }

        OutboxProcessor processor = CreateOutboxProcessor(new OutboxOptions { BatchSize = 2 });

        List<int> claimed = [];
        for (int poll = 0; poll < 4; poll++)
        {
            claimed.Add(await processor.ProcessBatchAsync(Ct));
        }

        claimed.ShouldBe([2, 2, 2, 0]);
        (await RowsAsync()).ShouldAllBe(static r => r.Status == OutboxMessageStatus.Processed);
    }

    [Fact]
    public async Task Stopping_mid_batch_releases_the_leases_of_the_unprocessed_messages()
    {
        // Distinct OccurredAtUtc, so the claim order is the insertion order: three issued rows, then three paid.
        var ids = new InvoiceId[3];
        for (int i = 0; i < ids.Length; i++)
        {
            ids[i] = await IssueInvoiceAsync();
            Time.Advance(TimeSpan.FromSeconds(1));
        }

        foreach (InvoiceId id in ids)
        {
            await PayAsync(id);
            Time.Advance(TimeSpan.FromSeconds(1));
        }

        _blocking.Block = true;   // the first paid row blocks after its receipt went out
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        Task<int> run = CreateOutboxProcessor().ProcessBatchAsync(stop.Token);
        await _blocking.Started.Task.WaitAsync(WaitBudget, Ct);
        await stop.CancelAsync();
        Exception? thrown = await Record.ExceptionAsync(() => run.WaitAsync(WaitBudget, Ct));

        thrown.ShouldBeAssignableTo<OperationCanceledException>();
        DateTimeOffset now = Time.GetUtcNow();
        List<OutboxMessage> rows = await RowsAsync();
        rows.Where(static r => r.Type == IssuedType).ShouldAllBe(static r => r.Status == OutboxMessageStatus.Processed);
        List<OutboxMessage> paid = [.. rows.Where(static r => r.Type == PaidType)];
        paid.Count.ShouldBe(3);
        // Released, not failed: pending, no attempt counted, due again right away — the one in flight too.
        paid.ShouldAllBe(r => r.Status == OutboxMessageStatus.Pending && r.Attempts == 0 && r.Error == null && r.NextAttemptAtUtc <= now);
        paid.Count(r => Credited(r).Contains(SendReceipt)).ShouldBe(1);   // the in-flight one keeps its durable credits
        await EmailSender.Received(1).SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // The next host claims all three at once and finishes the in-flight one without a second receipt.
        _blocking.Block = false;
        (await CreateOutboxProcessor().ProcessBatchAsync(Ct)).ShouldBe(3);
        (await RowsAsync()).ShouldAllBe(static r => r.Status == OutboxMessageStatus.Processed);
        await EmailSender.Received(3).SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_bookkeeping_save_costs_its_own_message_and_the_rest_of_the_batch_goes_on()
    {
        // Oldest first, so the faulted row is near the head of the batch and the second invoice's rows follow it.
        await PayNewInvoiceAsync();
        Time.Advance(TimeSpan.FromSeconds(1));
        InvoiceId second = await PayNewInvoiceAsync();
        OutboxMessage faulted = (await RowsAsync()).First(static r => r.Type == PaidType);
        _bookkeepingFault.MessageId = faulted.Id;   // the database refuses every save of this row from here on
        var options = new OutboxOptions();

        (await CreateOutboxProcessor(options).ProcessBatchAsync(Ct)).ShouldBe(4);

        // Its first checkpoint failed, then the save of that failure: nothing about the attempt is durable,
        // so the row holds exactly what the claim gave it — the lease — and comes back when that expires.
        _bookkeepingFault.Failures.ShouldBe(2);
        OutboxMessage stranded = await RowAsync(faulted.Id);
        stranded.Status.ShouldBe(OutboxMessageStatus.Pending);
        stranded.Attempts.ShouldBe(0);
        stranded.CompletedHandlers.ShouldBeNull();
        stranded.NextAttemptAtUtc.ShouldBe(Time.GetUtcNow() + options.LeaseDuration);

        // Everything after it in the same batch was dispatched and booked as if nothing had happened.
        (await RowsAsync()).Where(r => r.Id != faulted.Id).ShouldAllBe(static r => r.Status == OutboxMessageStatus.Processed);
        await EmailSender.Received(1).SendAsync("billing@acme.com", Arg.Any<string>(), Arg.Is<string>(body => body.Contains(second.ToString())), Arg.Any<CancellationToken>());

        // Not due before the lease runs out; claimed and finished once it has.
        _bookkeepingFault.MessageId = null;
        (await CreateOutboxProcessor(options).ProcessBatchAsync(Ct)).ShouldBe(0);
        Time.Advance(options.LeaseDuration);
        (await CreateOutboxProcessor(options).ProcessBatchAsync(Ct)).ShouldBe(1);
        (await RowAsync(faulted.Id)).Status.ShouldBe(OutboxMessageStatus.Processed);
    }

    [Fact]
    public async Task The_backlog_gauges_report_the_pending_count_and_the_oldest_age()
    {
        using var recorder = new MetricRecorder(MeterFactory);
        // Two rows in back-off (pending, not due) of different ages, and a processed one the gauges ignore.
        await InsertAsync(
            Row(PaidType, "{}", occurredAt: Now - TimeSpan.FromMinutes(10), nextAttemptAt: Now + TimeSpan.FromMinutes(1)),
            Row(PaidType, "{}", occurredAt: Now - TimeSpan.FromMinutes(1), nextAttemptAt: Now + TimeSpan.FromMinutes(1)),
            Row(IssuedType, "{}", occurredAt: Now - TimeSpan.FromHours(1), status: OutboxMessageStatus.Processed));

        (await CreateOutboxProcessor().ProcessBatchAsync(Ct)).ShouldBe(0);
        recorder.RecordObservableInstruments();

        recorder.Last("trellis.outbox.pending").ShouldBe(2);
        recorder.Last("trellis.outbox.oldest_pending_age").ShouldBe(TimeSpan.FromMinutes(10).TotalSeconds);
    }

    private async Task<InvoiceId> PayNewInvoiceAsync()
    {
        InvoiceId id = await IssueInvoiceAsync();
        await PayAsync(id);
        return id;
    }

    private async Task<InvoiceId> IssueInvoiceAsync()
    {
        InvoiceId id = (await Services.GetRequiredService<ICommandHandler<CreateInvoiceCommand, InvoiceId>>()
            .Handle(new CreateInvoiceCommand("ACME", "billing@acme.com", "USD"), Ct)).Value;
        (await Services.GetRequiredService<ICommandHandler<AddInvoiceLineCommand>>()
            .Handle(new AddInvoiceLineCommand(id, "Line", 10m), Ct)).IsSuccess.ShouldBeTrue();
        (await Services.GetRequiredService<ICommandHandler<IssueInvoiceCommand>>()
            .Handle(new IssueInvoiceCommand(id, Time.GetUtcNow().AddDays(14)), Ct)).IsSuccess.ShouldBeTrue();
        return id;
    }

    private async Task PayAsync(InvoiceId id)
    {
        using IServiceScope scope = CreateScope();
        (await scope.ServiceProvider.GetRequiredService<ICommandHandler<PayInvoiceCommand>>()
            .Handle(new PayInvoiceCommand(id), Ct)).IsSuccess.ShouldBeTrue();
    }

    private async Task<OutboxMessage> RowAsync(string type)
    {
        using IServiceScope scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<OutboxMessage>()
            .AsNoTracking()
            .SingleAsync(m => m.Type == type, Ct);
    }

    private async Task<OutboxMessage> RowAsync(string type, Guid except)
    {
        using IServiceScope scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<OutboxMessage>()
            .AsNoTracking()
            .SingleAsync(m => m.Type == type && m.Id != except, Ct);
    }

    private async Task<OutboxMessage> RowAsync(Guid id)
    {
        using IServiceScope scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<OutboxMessage>()
            .AsNoTracking()
            .SingleAsync(m => m.Id == id, Ct);
    }

    private async Task<List<OutboxMessage>> RowsAsync()
    {
        using IServiceScope scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<OutboxMessage>()
            .AsNoTracking()
            .OrderBy(static m => m.OccurredAtUtc)
            .ToListAsync(Ct);
    }

    private async Task<int> InvoiceCountAsync()
    {
        using IServiceScope scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<Invoice>().CountAsync(Ct);
    }

    private async Task InsertAsync(params OutboxMessage[] rows)
    {
        using IServiceScope scope = CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Set<OutboxMessage>().AddRange(rows);
        await context.SaveChangesAsync(Ct);
    }

    private static OutboxMessage Row(
        string type,
        string content,
        DateTimeOffset? occurredAt = null,
        DateTimeOffset? nextAttemptAt = null,
        OutboxMessageStatus status = OutboxMessageStatus.Pending)
        => new()
        {
            Id = Guid.CreateVersion7(),
            Type = type,
            Content = content,
            Status = status,
            OccurredAtUtc = occurredAt ?? Now,
            NextAttemptAtUtc = nextAttemptAt ?? occurredAt ?? Now,
        };

    private static HashSet<string> Credited(OutboxMessage message)
        => message.CompletedHandlers is { Length: > 0 } json
            ? JsonSerializer.Deserialize<HashSet<string>>(json)!
            : [];

    private static void ShouldCredit(OutboxMessage message, params string[] handlers)
    {
        HashSet<string> credited = Credited(message);
        foreach (string handler in handlers)
        {
            credited.Contains(handler).ShouldBeTrue($"'{handler}' should be credited; CompletedHandlers = {message.CompletedHandlers}");
        }
    }

    private static void ShouldNotCredit(OutboxMessage message, params string[] handlers)
    {
        HashSet<string> credited = Credited(message);
        foreach (string handler in handlers)
        {
            credited.Contains(handler).ShouldBeFalse($"'{handler}' should not be credited; CompletedHandlers = {message.CompletedHandlers}");
        }
    }

    private static KeyValuePair<string, object?> Tag(string key, object? value) => KeyValuePair.Create(key, value);

    /// <summary>Switches <see cref="WritingPaidHandler"/> on for one test (it is registered for all of them).</summary>
    internal sealed class WritingSwitch
    {
        public bool Enabled { get; set; }
    }

    /// <summary>
    /// A handler that does a unit-of-work write and nothing else — the case the checkpoint exists for. It never
    /// calls SaveChanges: the outbox commits the write at this handler's checkpoint, inside the ambient tenant.
    /// </summary>
    [DomainEventHandlerName(WritingName)]
    internal sealed class WritingPaidHandler(IInvoiceRepository repository, WritingSwitch writing)
        : IDomainEventHandler<InvoicePaidDomainEvent>
    {
        public const string CustomerName = "Written by the outbox handler";

        public Task Handle(InvoicePaidDomainEvent domainEvent, CancellationToken cancellationToken)
        {
            if (writing.Enabled)
            {
                repository.Add(Invoice.Create(CustomerName, EmailAddress.Create("copy@acme.com").Value, Currency.Create("USD").Value).Value);
            }

            return Task.CompletedTask;
        }
    }

    [DomainEventHandlerName(FlakyName)]
    private sealed class FlakyPaidHandler : IDomainEventHandler<InvoicePaidDomainEvent>
    {
        private int _calls;

        public bool Throw { get; set; }

        public Func<Exception> Exception { get; set; } = static () => new InvalidOperationException("downstream down");

        public int Calls => Volatile.Read(ref _calls);

        public Task Handle(InvoicePaidDomainEvent domainEvent, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _calls);
            return Throw ? Task.FromException(Exception()) : Task.CompletedTask;
        }
    }

    /// <summary>
    /// Fails every save of one outbox row that runs outside a transaction — its checkpoints, its failure, its
    /// dead letter, its Processed mark — the way a database that went away mid-batch would. The claim stamps
    /// its lease inside the claim transaction, so it is left alone.
    /// </summary>
    private sealed class BookkeepingFault : SaveChangesInterceptor
    {
        private int _failures;

        public Guid? MessageId { get; set; }

        public int Failures => Volatile.Read(ref _failures);

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (MessageId is { } id
                && eventData.Context is { Database.CurrentTransaction: null } context
                && context.ChangeTracker.Entries<OutboxMessage>().Any(entry => entry.Entity.Id == id))
            {
                Interlocked.Increment(ref _failures);
                throw new DbUpdateException("Simulated: the database is unavailable.");
            }

            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    /// <summary>Signals that it is running, then waits for its token: the shape of a handler stuck on a dependency.</summary>
    [DomainEventHandlerName("test.blocking")]
    private sealed class BlockingPaidHandler : IDomainEventHandler<InvoicePaidDomainEvent>
    {
        public bool Block { get; set; }

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task Handle(InvoicePaidDomainEvent domainEvent, CancellationToken cancellationToken)
        {
            if (!Block)
            {
                return;
            }

            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }
}
