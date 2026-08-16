using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Trellis.Application.Abstractions;
using Xunit;

namespace Trellis.IntegrationTests;

/// <summary>The at-most-once guarantee lives or dies on real database semantics — no mocks here.</summary>
public sealed class IdempotencyStoreTests(DatabaseFixture database) : IntegrationTestBase(database)
{
    [Fact]
    public async Task Reserving_the_same_key_twice_fails_the_second_time()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<IIdempotencyStore>();

        (await store.TryReserveAsync("key-1", ct)).ShouldBeTrue();

        using var second = CreateScope();
        (await second.ServiceProvider.GetRequiredService<IIdempotencyStore>().TryReserveAsync("key-1", ct)).ShouldBeFalse();
    }

    [Fact]
    public async Task Concurrent_reservations_of_one_key_grant_exactly_one_winner()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        using var scopeA = CreateScope();
        using var scopeB = CreateScope();
        Task<bool> a = scopeA.ServiceProvider.GetRequiredService<IIdempotencyStore>().TryReserveAsync("race", ct);
        Task<bool> b = scopeB.ServiceProvider.GetRequiredService<IIdempotencyStore>().TryReserveAsync("race", ct);

        bool[] outcomes = await Task.WhenAll(a, b);

        outcomes.Count(static won => won).ShouldBe(1);
    }

    [Fact]
    public async Task Releasing_a_key_makes_it_reservable_again()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var store = Services.GetRequiredService<IIdempotencyStore>();

        (await store.TryReserveAsync("released", ct)).ShouldBeTrue();
        await store.ReleaseAsync("released", ct);

        using var retry = CreateScope();
        (await retry.ServiceProvider.GetRequiredService<IIdempotencyStore>().TryReserveAsync("released", ct)).ShouldBeTrue();
    }
}
