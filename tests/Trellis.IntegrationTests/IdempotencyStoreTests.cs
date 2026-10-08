using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Trellis.Application.Abstractions;
using Trellis.Persistence.Maintenance;
using Trellis.SharedKernel.Domain;
using Trellis.SharedKernel.Results;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>
/// The at-most-once guarantee lives or dies on real database semantics — no mocks here. A reservation
/// is nothing until its unit of work commits, the commit race is decided by the primary key, and the
/// completed response round-trips byte for byte (ADR-0018).
/// </summary>
public sealed class IdempotencyStoreTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    private const string Fingerprint = "0F1E2D3C4B5A69788796A5B4C3D2E1F00F1E2D3C4B5A69788796A5B4C3D2E1F0";

    [Fact]
    public async Task Reserve_commit_and_complete_walk_the_key_through_its_three_states()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<IIdempotencyStore>();

        (await store.FindAsync("key-1", ct)).ShouldBeSameAs(IdempotencyLookup.None);

        store.Reserve("key-1", Fingerprint);
        // Tracked only: another unit of work sees nothing until this one commits.
        (await FindInNewScopeAsync("key-1")).ShouldBeSameAs(IdempotencyLookup.None);

        (await Services.GetRequiredService<IUnitOfWork>().CommitAsync(ct)).IsSuccess.ShouldBeTrue();
        var inFlight = (await FindInNewScopeAsync("key-1")).ShouldBeOfType<IdempotencyLookup.InFlight>();
        inFlight.ReservedAtUtc.ShouldBe(Now);

        var response = new StoredResponse(
            StatusCode: 201,
            ContentType: "application/json; charset=utf-8",
            Body: Encoding.UTF8.GetBytes("""{"id":"0199c0de-0000-7000-8000-000000000001"}"""),
            Location: "/api/v1/invoices/0199c0de-0000-7000-8000-000000000001");
        await store.CompleteAsync("key-1", response, ct);

        var completed = (await FindInNewScopeAsync("key-1")).ShouldBeOfType<IdempotencyLookup.Completed>();
        completed.RequestFingerprint.ShouldBe(Fingerprint);
        completed.Response.StatusCode.ShouldBe(201);
        completed.Response.ContentType.ShouldBe(response.ContentType);
        completed.Response.Body.ShouldBe(response.Body);
        completed.Response.Location.ShouldBe(response.Location);
        completed.Response.IsReplayable.ShouldBeTrue();
    }

    [Fact]
    public async Task Completing_a_reservation_its_unit_of_work_never_committed_writes_nothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<IIdempotencyStore>();

        // The command failed before its commit: the filter still reports the outcome, and nothing may land.
        store.Reserve("failed", Fingerprint);
        await store.CompleteAsync("failed", StoredResponse.Unavailable(400), ct);

        (await FindInNewScopeAsync("failed")).ShouldBeSameAs(IdempotencyLookup.None);

        // The key is free: the client's retry reserves it afresh.
        using IServiceScope retry = CreateScope();
        retry.ServiceProvider.GetRequiredService<IIdempotencyStore>().Reserve("failed", Fingerprint);
        (await retry.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(ct)).IsSuccess.ShouldBeTrue();
        (await FindInNewScopeAsync("failed")).ShouldBeOfType<IdempotencyLookup.InFlight>();
    }

    [Fact]
    public async Task Two_units_of_work_reserving_one_key_commit_exactly_one_and_the_loser_gets_Duplicate()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using IServiceScope scopeA = CreateScope();
        using IServiceScope scopeB = CreateScope();
        scopeA.ServiceProvider.GetRequiredService<IIdempotencyStore>().Reserve("race", Fingerprint);
        scopeB.ServiceProvider.GetRequiredService<IIdempotencyStore>().Reserve("race", Fingerprint);

        Result[] outcomes = await Task.WhenAll(
            scopeA.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(ct),
            scopeB.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(ct));

        outcomes.Count(static outcome => outcome.IsSuccess).ShouldBe(1);
        outcomes.Single(static outcome => outcome.IsFailure).Error.ShouldBe(IdempotencyErrors.Duplicate);
        (await FindInNewScopeAsync("race")).ShouldBeOfType<IdempotencyLookup.InFlight>();
    }

    [Fact]
    public async Task A_completion_without_a_body_is_known_but_not_replayable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<IIdempotencyStore>();
        store.Reserve("large", Fingerprint);
        (await Services.GetRequiredService<IUnitOfWork>().CommitAsync(ct)).IsSuccess.ShouldBeTrue();

        await store.CompleteAsync("large", StoredResponse.Unavailable(200, "application/json; charset=utf-8"), ct);

        var completed = (await FindInNewScopeAsync("large")).ShouldBeOfType<IdempotencyLookup.Completed>();
        completed.Response.StatusCode.ShouldBe(200);
        completed.Response.Body.ShouldBeNull();
        completed.Response.IsReplayable.ShouldBeFalse();
    }

    [Fact]
    public async Task Retention_sweeps_a_completed_reservation_by_its_creation_time()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var retention = new RetentionOptions();
        var store = Services.GetRequiredService<IIdempotencyStore>();
        store.Reserve("old", Fingerprint);
        (await Services.GetRequiredService<IUnitOfWork>().CommitAsync(ct)).IsSuccess.ShouldBeTrue();

        // Completing later does not renew the key: the window counts from the reservation.
        Time.Advance(retention.IdempotencyKeyAge - TimeSpan.FromMinutes(1));
        await store.CompleteAsync("old", StoredResponse.Unavailable(204), ct);
        Time.Advance(TimeSpan.FromMinutes(2));

        using (IServiceScope recent = CreateScope())
        {
            recent.ServiceProvider.GetRequiredService<IIdempotencyStore>().Reserve("recent", Fingerprint);
            (await recent.ServiceProvider.GetRequiredService<IUnitOfWork>().CommitAsync(ct)).IsSuccess.ShouldBeTrue();
        }

        var janitor = new DataRetentionJanitor(
            Services.GetRequiredService<IServiceScopeFactory>(), Time, retention, NullLogger<DataRetentionJanitor>.Instance);
        await janitor.SweepAsync(ct);

        (await FindInNewScopeAsync("old")).ShouldBeSameAs(IdempotencyLookup.None);
        (await FindInNewScopeAsync("recent")).ShouldBeOfType<IdempotencyLookup.InFlight>();
    }

    private async Task<IdempotencyLookup> FindInNewScopeAsync(string key)
    {
        using IServiceScope scope = CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IIdempotencyStore>().FindAsync(key, TestContext.Current.CancellationToken);
    }
}
