using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Trellis.Persistence;
using Trellis.Persistence.Idempotency;
using Trellis.Persistence.Identity;
using Trellis.Persistence.Maintenance;
using Trellis.Persistence.Outbox;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// Every sweep predicate pinned at its cutoff: a row one second older than its window goes, a row one
/// second younger stays, and survivors are asserted by id so a predicate that deletes the wrong row (or
/// too many) cannot hide behind a count. Pending outbox rows are never swept, however old.
/// </summary>
public sealed class DataRetentionJanitorTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    private static readonly TimeSpan OneSecond = TimeSpan.FromSeconds(1);

    private readonly RetentionOptions _options = new();

    [Fact]
    public async Task A_sweep_deletes_exactly_the_rows_older_than_their_window()
    {
        DateTimeOffset now = Time.GetUtcNow();
        DateTimeOffset processedCutoff = now - _options.ProcessedOutboxAge;
        DateTimeOffset deadLetterCutoff = now - _options.DeadLetteredOutboxAge;
        DateTimeOffset keyCutoff = now - _options.IdempotencyKeyAge;
        DateTimeOffset tokenCutoff = now - _options.DeadRefreshTokenAge;

        Guid userId = await SeedUserAsync();

        OutboxMessage processedExpired = Outbox(OutboxMessageStatus.Processed, occurredAt: processedCutoff - TimeSpan.FromDays(1), processedAt: processedCutoff - OneSecond);
        OutboxMessage processedKept = Outbox(OutboxMessageStatus.Processed, occurredAt: processedCutoff - TimeSpan.FromDays(1), processedAt: processedCutoff + OneSecond);
        OutboxMessage deadLetteredExpired = Outbox(OutboxMessageStatus.DeadLettered, occurredAt: deadLetterCutoff - OneSecond);
        OutboxMessage deadLetteredKept = Outbox(OutboxMessageStatus.DeadLettered, occurredAt: deadLetterCutoff + OneSecond);

        // Pending is never terminal: older than every window, and even carrying a stale ProcessedAtUtc
        // from some earlier bookkeeping, it still has handlers to run.
        OutboxMessage pendingAncient = Outbox(OutboxMessageStatus.Pending, occurredAt: now - TimeSpan.FromDays(400));
        OutboxMessage pendingWithStaleProcessedAt = Outbox(OutboxMessageStatus.Pending, occurredAt: now - TimeSpan.FromDays(400), processedAt: now - TimeSpan.FromDays(400));

        IdempotencyKey keyExpired = Key(createdAt: keyCutoff - OneSecond);
        IdempotencyKey keyKept = Key(createdAt: keyCutoff + OneSecond);

        RefreshToken consumedExpired = Token(userId, expiresAt: now + TimeSpan.FromDays(1), consumedAt: tokenCutoff - OneSecond);
        RefreshToken consumedKept = Token(userId, expiresAt: tokenCutoff - TimeSpan.FromDays(1), consumedAt: tokenCutoff + OneSecond);
        RefreshToken unconsumedExpired = Token(userId, expiresAt: tokenCutoff - OneSecond);
        RefreshToken unconsumedKept = Token(userId, expiresAt: tokenCutoff + OneSecond);
        RefreshToken live = Token(userId, expiresAt: now + TimeSpan.FromDays(7));

        using (IServiceScope scope = CreateScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Set<OutboxMessage>().AddRange(processedExpired, processedKept, deadLetteredExpired, deadLetteredKept, pendingAncient, pendingWithStaleProcessedAt);
            context.Set<IdempotencyKey>().AddRange(keyExpired, keyKept);
            context.Set<RefreshToken>().AddRange(consumedExpired, consumedKept, unconsumedExpired, unconsumedKept, live);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await NewJanitor().SweepAsync(TestContext.Current.CancellationToken);

        using (IServiceScope scope = CreateScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            (await context.Set<OutboxMessage>().Select(static m => m.Id).ToListAsync(TestContext.Current.CancellationToken))
                .ShouldBe([processedKept.Id, deadLetteredKept.Id, pendingAncient.Id, pendingWithStaleProcessedAt.Id], ignoreOrder: true);

            (await context.Set<IdempotencyKey>().Select(static k => k.Key).ToListAsync(TestContext.Current.CancellationToken))
                .ShouldBe([keyKept.Key]);

            (await context.Set<RefreshToken>().Select(static t => t.Id).ToListAsync(TestContext.Current.CancellationToken))
                .ShouldBe([consumedKept.Id, unconsumedKept.Id, live.Id], ignoreOrder: true);
        }
    }

    [Fact]
    public async Task A_sweep_with_nothing_expired_deletes_nothing()
    {
        DateTimeOffset now = Time.GetUtcNow();
        OutboxMessage fresh = Outbox(OutboxMessageStatus.Processed, occurredAt: now, processedAt: now);

        using (IServiceScope scope = CreateScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Set<OutboxMessage>().Add(fresh);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await NewJanitor().SweepAsync(TestContext.Current.CancellationToken);

        using IServiceScope readScope = CreateScope();
        (await readScope.ServiceProvider.GetRequiredService<AppDbContext>().Set<OutboxMessage>()
                .Select(static m => m.Id).ToListAsync(TestContext.Current.CancellationToken))
            .ShouldBe([fresh.Id]);
    }

    [Fact]
    public async Task The_janitor_waits_the_initial_delay_then_sweeps_every_interval_on_the_injected_clock()
    {
        DateTimeOffset now = Time.GetUtcNow();
        OutboxMessage expired = Outbox(OutboxMessageStatus.Processed, occurredAt: now - TimeSpan.FromDays(10), processedAt: now - TimeSpan.FromDays(10));

        using (IServiceScope scope = CreateScope())
        {
            AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Set<OutboxMessage>().Add(expired);
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var clock = new TimerRecordingClock(Time);
        DataRetentionJanitor janitor = NewJanitor(clock);
        await janitor.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            // The loop's first wait is the initial delay; only the fake clock can end it.
            (await clock.NextTimerAsync()).ShouldBe(_options.InitialDelay);
            Time.Advance(_options.InitialDelay - OneSecond);
            (await OutboxRowExistsAsync(expired.Id)).ShouldBeTrue("nothing is swept before the initial delay elapses");

            // The next wait is only scheduled once the sweep returned, so the row is gone by then.
            Time.Advance(OneSecond);
            (await clock.NextTimerAsync()).ShouldBe(_options.SweepInterval);
            (await OutboxRowExistsAsync(expired.Id)).ShouldBeFalse();
        }
        finally
        {
            await janitor.StopAsync(CancellationToken.None);
            janitor.Dispose();
        }
    }

    private DataRetentionJanitor NewJanitor(TimeProvider? clock = null)
        => new(
            Services.GetRequiredService<IServiceScopeFactory>(),
            clock ?? Time,
            _options,
            NullLogger<DataRetentionJanitor>.Instance);

    private async Task<bool> OutboxRowExistsAsync(Guid id)
    {
        using IServiceScope scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<AppDbContext>().Set<OutboxMessage>()
            .AnyAsync(m => m.Id == id, TestContext.Current.CancellationToken);
    }

    private async Task<Guid> SeedUserAsync()
    {
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            UserName = "janitor@integration.test",
            Email = "janitor@integration.test",
#if (UseMultitenancy)
            TenantId = "janitor-tenant",
#endif
        };

        using IServiceScope scope = CreateScope();
        AppDbContext context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        context.Users.Add(user);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return user.Id;
    }

    private static OutboxMessage Outbox(OutboxMessageStatus status, DateTimeOffset occurredAt, DateTimeOffset? processedAt = null)
        => new()
        {
            Id = Guid.CreateVersion7(),
            Type = "janitor.test.v1",
            Content = "{}",
            Status = status,
            OccurredAtUtc = occurredAt,
            NextAttemptAtUtc = occurredAt,
            ProcessedAtUtc = processedAt,
        };

    private static IdempotencyKey Key(DateTimeOffset createdAt)
        => new() { Key = $"janitor-{Guid.CreateVersion7():N}", CreatedAtUtc = createdAt };

    private static RefreshToken Token(Guid userId, DateTimeOffset expiresAt, DateTimeOffset? consumedAt = null)
        => new()
        {
            Id = Guid.CreateVersion7(),
            UserId = userId,
            TokenHash = Guid.CreateVersion7().ToString("N") + Guid.CreateVersion7().ToString("N"),
            ExpiresAtUtc = expiresAt,
            ConsumedAtUtc = consumedAt,
        };

    /// <summary>
    /// The fixture's fake clock, reporting the due time of every timer created on it. <c>Task.Delay</c>
    /// on a <see cref="TimeProvider"/> creates one per wait, which tells the test that the janitor's loop
    /// has reached its next wait — <c>BackgroundService</c> runs <c>ExecuteAsync</c> on the thread pool,
    /// so advancing the clock before the wait exists would be lost.
    /// </summary>
    private sealed class TimerRecordingClock(TimeProvider inner) : TimeProvider
    {
        private readonly Channel<TimeSpan> _dueTimes = Channel.CreateUnbounded<TimeSpan>();

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ITimer timer = inner.CreateTimer(callback, state, dueTime, period);
            _dueTimes.Writer.TryWrite(dueTime);
            return timer;
        }

        public async Task<TimeSpan> NextTimerAsync()
            => await _dueTimes.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask()
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
    }
}
