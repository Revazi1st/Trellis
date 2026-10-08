using Shouldly;
using Trellis.Persistence.Outbox;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// The retry schedule is pure arithmetic over <see cref="OutboxOptions"/>, so it is pinned here without
/// a database: the default series an operator reads in the docs, the cap, and the overflow guard.
/// </summary>
public sealed class OutboxBackoffTests
{
    [Fact]
    public void The_default_schedule_doubles_from_thirty_seconds_and_caps_at_fifteen_minutes()
    {
        var options = new OutboxOptions();

        TimeSpan[] series = [.. Enumerable.Range(1, 7).Select(attempt => OutboxBackoff.DelayAfter(options, attempt))];

        series.ShouldBe(
        [
            TimeSpan.FromSeconds(30),
            TimeSpan.FromMinutes(1),
            TimeSpan.FromMinutes(2),
            TimeSpan.FromMinutes(4),
            TimeSpan.FromMinutes(8),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(15),
        ]);

        // Seven waits before the eighth, fatal failure: the documented ≈45-minute dead-letter window.
        series.Aggregate(TimeSpan.Zero, static (sum, delay) => sum + delay).ShouldBe(TimeSpan.FromMinutes(45.5));
    }

    [Fact]
    public void The_largest_allowed_attempt_count_does_not_overflow()
    {
        var options = new OutboxOptions { MaxAttempts = 50, BaseRetryDelay = TimeSpan.FromHours(1), MaxRetryDelay = TimeSpan.FromDays(1) };

        Should.NotThrow(() => OutboxBackoff.DelayAfter(options, 50)).ShouldBe(TimeSpan.FromDays(1));
    }

    [Fact]
    public void A_failed_attempt_count_below_one_is_treated_as_the_first_failure()
        => OutboxBackoff.DelayAfter(new OutboxOptions(), 0).ShouldBe(TimeSpan.FromSeconds(30));

    [Fact]
    public void The_lease_is_the_dispatch_budget_of_a_full_batch_plus_one_slot()
    {
        new OutboxOptions().LeaseDuration.ShouldBe(TimeSpan.FromMinutes(10.5));
        new OutboxOptions { BatchSize = 2, DispatchTimeout = TimeSpan.FromSeconds(5) }.LeaseDuration.ShouldBe(TimeSpan.FromSeconds(15));
    }
}
