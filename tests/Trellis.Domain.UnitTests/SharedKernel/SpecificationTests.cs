using Shouldly;
using Trellis.SharedKernel.Specifications;
using Xunit;

namespace Trellis.Domain.UnitTests.SharedKernel;

public sealed class SpecificationTests
{
    private sealed record Item(string Name, int Rank, bool Active);

    private static readonly Item[] Items =
    [
        new("delta", 4, true),
        new("alpha", 2, true),
        new("charlie", 2, false),
        new("bravo", 1, true),
        new("echo", 3, true),
    ];

    private sealed class ActiveByRankThenName : Specification<Item>
    {
        public ActiveByRankThenName()
        {
            Where(i => i.Active);
            OrderBy(i => i.Rank);
            OrderBy(i => i.Name);
        }
    }

    private sealed class PagedDescending : Specification<Item>
    {
        public PagedDescending()
        {
            OrderByDescending(i => i.Rank);
            Paginate(skip: 1, take: 2);
        }
    }

    private sealed class TwoWheres : Specification<Item>
    {
        public TwoWheres()
        {
            Where(i => i.Active);
            Where(i => i.Rank >= 2);
        }
    }

    [Fact]
    public void Where_filters_and_orderings_chain_as_then_by()
        => new ActiveByRankThenName().Evaluate(Items)
            .Select(i => i.Name)
            .ShouldBe(["bravo", "alpha", "echo", "delta"]);

    [Fact]
    public void Descending_order_with_paging_windows_the_result()
        => new PagedDescending().Evaluate(Items)
            .Select(i => i.Name)
            .ShouldBe(["echo", "alpha"]);

    [Fact]
    public void Multiple_wheres_are_and_composed()
        => new TwoWheres().Evaluate(Items)
            .Select(i => i.Name)
            .ShouldBe(["delta", "alpha", "echo"], ignoreOrder: true);

    [Fact]
    public void ApplyTo_preserves_queryable_provider_expressions()
    {
        IQueryable<Item> query = new ActiveByRankThenName().ApplyTo(Items.AsQueryable());

        query.Expression.ToString().ShouldContain("OrderBy");
        query.Expression.ToString().ShouldContain("ThenBy");
    }
}
