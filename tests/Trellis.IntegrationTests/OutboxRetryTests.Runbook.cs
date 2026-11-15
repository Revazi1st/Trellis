using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Trellis.Persistence;
using Trellis.Persistence.Outbox;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// The one test of <see cref="OutboxRetryTests"/> that writes the engine's SQL: it runs the runbook's
/// re-drive statement exactly as <c>docs/deployment.md</c> prints it. It sits in a file of its own so that
/// the engine rule's allowlist (<c>DatabaseEngineTests</c>) exempts these lines and not the whole class.
/// </summary>
public sealed partial class OutboxRetryTests
{
    [Fact]
    public async Task The_runbooks_redrive_statement_gives_a_dead_letter_a_fresh_budget_without_resending_the_credited_receipt()
    {
        // One attempt only: the receipt goes out and is credited at its checkpoint, the flaky handler fails,
        // and the row is dead-lettered with the credit kept.
        var options = new OutboxOptions { MaxAttempts = 1 };
        _flaky.Throw = true;
        await PayNewInvoiceAsync();
        await CreateOutboxProcessor(options).ProcessBatchAsync(Ct);
        OutboxMessage dead = await RowAsync(PaidType);
        dead.Status.ShouldBe(OutboxMessageStatus.DeadLettered);
        dead.Attempts.ShouldBe(1);
        ShouldCredit(dead, SendReceipt, RecordMetrics, BlockingName);
        ShouldNotCredit(dead, FlakyName);
        await EmailSender.Received(1).SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // The dependency is fixed and the operator runs the single-row statement of docs/deployment.md
        // ("Outbox operations") as written there, with '<id>' filled in.
        _flaky.Throw = false;
        using (IServiceScope scope = CreateScope())
        {
            int affected = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.ExecuteSqlAsync($"""
                UPDATE "OutboxMessages" SET "Status" = 0, "Attempts" = 0, "Error" = NULL, "NextAttemptAtUtc" = now()
                WHERE "Status" = 2 AND "Id" = {dead.Id}
                """, Ct);
            affected.ShouldBe(1);
        }

        OutboxMessage redriven = await RowAsync(PaidType);
        redriven.Status.ShouldBe(OutboxMessageStatus.Pending);
        redriven.Attempts.ShouldBe(0);
        redriven.Error.ShouldBeNull();
        redriven.CompletedHandlers.ShouldBe(dead.CompletedHandlers);   // kept on purpose: the credited handlers stay skipped

        // now() is the database's wall clock, not the test's: the row is due once the processor's clock caught up.
        if (redriven.NextAttemptAtUtc > Time.GetUtcNow())
        {
            Time.SetUtcNow(redriven.NextAttemptAtUtc);
        }

        (await CreateOutboxProcessor(options).ProcessBatchAsync(Ct)).ShouldBe(1);

        OutboxMessage finished = await RowAsync(PaidType);
        finished.Status.ShouldBe(OutboxMessageStatus.Processed);
        finished.Attempts.ShouldBe(0);
        finished.Error.ShouldBeNull();
        ShouldCredit(finished, SendReceipt, RecordMetrics, FlakyName, BlockingName);
        _flaky.Calls.ShouldBe(2);   // the failed try and the one the re-drive owed
        await EmailSender.Received(1).SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());   // not sent again
    }
}
